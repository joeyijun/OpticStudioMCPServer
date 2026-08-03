param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$bridge = Join-Path $root "src\ZemaxMCP.HttpBridge\bin\$Configuration\net48\ZemaxMCP.HttpBridge.exe"
if (-not (Test-Path -LiteralPath $bridge)) { throw "HTTP bridge build output is missing." }

$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$probe.Start(); $port = ([Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ZemaxMCP-streamable-" + [guid]::NewGuid().ToString("N"))
$fakeServer = Join-Path $testRoot "FakeMcpServer.exe"
$fixture = Join-Path $PSScriptRoot "fixtures\FakeMcpServer.cs"
$oldToken = $env:ZEMAX_MCP_TOKEN
$process = $null

function Get-Response([scriptblock]$action) {
  try { return & $action }
  catch {
    if ($_.Exception.Response) { return $_.Exception.Response }
    throw
  }
}

try {
  New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
  $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
  if (-not (Test-Path -LiteralPath $csc)) { throw "The .NET Framework C# compiler is required for streamable HTTP verification." }
  & $csc /nologo /target:exe /out:$fakeServer $fixture
  if ($LASTEXITCODE -ne 0) { throw "Could not compile the streamable HTTP test server." }
  $env:ZEMAX_MCP_TOKEN = "streamable-verifier-token"
  $arguments = @("--server", $fakeServer, "--host", "127.0.0.1", "--port", [string]$port, "--log-dir", $testRoot, "--snapshot-dir", (Join-Path $testRoot "snapshots"), "--read-only", "true")
  $process = Start-Process -FilePath $bridge -ArgumentList $arguments -PassThru -WindowStyle Hidden
  $url = "http://127.0.0.1:$port/mcp/"
  $headers = @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream" }
  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  do { Start-Sleep -Milliseconds 100; $health = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($url + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } -TimeoutSec 1 } }
  while ($health.StatusCode -ne 200 -and [DateTime]::UtcNow -lt $deadline)

  $initialize = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"streamable-verifier","version":"1"}}}'
  $init = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  if ($init.StatusCode -ne 200 -or $init.Content -notmatch 'fake-mcp') { throw "Initialize did not return a JSON MCP response." }
  $session = [string]$init.Headers["Mcp-Session-Id"]
  if ([string]::IsNullOrWhiteSpace($session)) { throw "Initialize did not establish an MCP session." }

  $sseHeaders = @{ Authorization = "Bearer streamable-verifier-token"; Accept = "text/event-stream"; "Mcp-Session-Id" = $session }
  $list = '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  $sse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $sseHeaders -Body $list }
  if ($sse.StatusCode -ne 200 -or $sse.Headers["Content-Type"] -notmatch "text/event-stream" -or $sse.Content -notmatch "event: message") { throw "SSE MCP response was not valid." }

  $badAccept = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/xml"; "Mcp-Session-Id" = $session } -Body $list }
  if ([int]$badAccept.StatusCode -ne 406) { throw "Unsupported Accept header was not rejected with HTTP 406." }

  $deleted = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Delete -Headers @{ Authorization = "Bearer streamable-verifier-token"; "Mcp-Session-Id" = $session } }
  if ($deleted.StatusCode -ne 200) { throw "Session DELETE did not return HTTP 200." }
  $afterDelete = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "Mcp-Session-Id" = $session } -Body $list }
  if ([int]$afterDelete.StatusCode -ne 404) { throw "Deleted MCP session was accepted." }

  $notification = '{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}'
  $accepted = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $notification }
  if ($accepted.StatusCode -ne 202) { throw "MCP notification did not return HTTP 202." }
  Write-Host "Streamable HTTP Accept negotiation, SSE response, session DELETE, and notification behavior verified."
}
finally {
  $env:ZEMAX_MCP_TOKEN = $oldToken
  if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; $process.WaitForExit(3000) | Out-Null }
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
