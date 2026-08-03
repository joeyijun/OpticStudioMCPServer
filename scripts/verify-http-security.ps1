param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$bridge = Join-Path $root "src\ZemaxMCP.HttpBridge\bin\$Configuration\net48\ZemaxMCP.HttpBridge.exe"
if (-not (Test-Path -LiteralPath $bridge)) { throw "HTTP bridge build output is missing." }

$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = ([Net.IPEndPoint]$probe.LocalEndpoint).Port
$probe.Stop()
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ZemaxMCP-http-security-" + [guid]::NewGuid().ToString("N"))
$oldToken = $env:ZEMAX_MCP_TOKEN
$process = $null
$failed = $false
try {
  New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
  $env:ZEMAX_MCP_TOKEN = "end-to-end-verifier-token"
  $arguments = @(
    "--server", "$env:SystemRoot\System32\more.com",
    "--host", "127.0.0.1",
    "--port", [string]$port,
    "--log-dir", $testRoot,
    "--snapshot-dir", (Join-Path $testRoot "snapshots"),
    "--read-only", "true"
  )
  $process = Start-Process -FilePath $bridge -ArgumentList $arguments -PassThru -WindowStyle Hidden
  $healthUrl = "http://127.0.0.1:$port/mcp/health"
  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  do {
    Start-Sleep -Milliseconds 100
    try {
      $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -Headers @{ Authorization = "Bearer end-to-end-verifier-token" } -TimeoutSec 1
    }
    catch { $response = $null }
  } while (-not $response -and [DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)
  if (-not $response -or $response.StatusCode -ne 200) { throw "The secured bridge did not become reachable." }
  $health = $response.Content | ConvertFrom-Json
  if (-not $health.authenticationRequired -or -not $health.originValidationEnabled -or -not $health.readOnly) {
    throw "The live health response did not report its security mode."
  }

  foreach ($case in @(
    @{ Name = "missing token"; Headers = @{}; Expected = 401 },
    @{ Name = "wrong token"; Headers = @{ Authorization = "Bearer wrong" }; Expected = 401 },
    @{ Name = "untrusted Origin"; Headers = @{ Authorization = "Bearer end-to-end-verifier-token"; Origin = "https://attacker.example" }; Expected = 403 }
  )) {
    try {
      Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -Headers $case.Headers -TimeoutSec 2 | Out-Null
      throw "The $($case.Name) request unexpectedly succeeded."
    }
    catch {
      $status = [int]$_.Exception.Response.StatusCode
      if ($status -ne $case.Expected) { throw "The $($case.Name) request returned HTTP $status instead of $($case.Expected)." }
    }
  }

  $sameHost = Invoke-WebRequest -UseBasicParsing -Uri $healthUrl -Headers @{
    Authorization = "Bearer end-to-end-verifier-token"
    Origin = "http://127.0.0.1:4567"
  } -TimeoutSec 2
  if ($sameHost.StatusCode -ne 200 -or $sameHost.Headers["Access-Control-Allow-Origin"] -ne "http://127.0.0.1:4567") {
    throw "A trusted same-host Origin was not accepted correctly."
  }
}
catch {
  $failed = $true
  Write-Warning "HTTP security verifier logs retained at $testRoot"
  throw
}
finally {
  $env:ZEMAX_MCP_TOKEN = $oldToken
  if ($process -and -not $process.HasExited) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $process.WaitForExit(3000) | Out-Null
  }
  if (-not $failed -and (Test-Path -LiteralPath $testRoot)) {
    Start-Sleep -Milliseconds 200
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}
Write-Host "Live HTTP Bearer authentication, Origin rejection, and security health reporting verified."
