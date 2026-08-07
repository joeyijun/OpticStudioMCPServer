param(
  [string]$Configuration = "Release",
  [ValidateSet("both", "stdio", "pipe")]
  [string]$Transport = "both"
)

$ErrorActionPreference = "Stop"
if ($Transport -eq "both") {
  & $PSCommandPath -Configuration $Configuration -Transport "stdio"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  & $PSCommandPath -Configuration $Configuration -Transport "pipe"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  Write-Host "Streamable HTTP regression passed against both stdio and private named-pipe backends."
  return
}
$root = Split-Path $PSScriptRoot -Parent
$bridge = Join-Path $root "src\ZemaxMCP.HttpBridge\bin\$Configuration\net48\ZemaxMCP.Host.exe"
if (-not (Test-Path -LiteralPath $bridge)) { throw "HTTP bridge build output is missing." }

$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$probe.Start(); $port = ([Net.IPEndPoint]$probe.LocalEndpoint).Port; $probe.Stop()
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ZemaxMCP-streamable-" + [guid]::NewGuid().ToString("N"))
$fakeServer = Join-Path $testRoot "FakeMcpServer.exe"
$fixture = Join-Path $PSScriptRoot "fixtures\FakeMcpServer.cs"
$oldToken = $env:ZEMAX_MCP_TOKEN
$process = $null
$sseFailureProcess = $null
$initializeWriteFailureProcess = $null

function Get-Response([scriptblock]$action) {
  try { return & $action }
  catch {
    if ($_.Exception.Response) {
      $response = $_.Exception.Response
      $content = ""
      try {
        $reader = [IO.StreamReader]::new($response.GetResponseStream())
        try { $content = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
      } catch { }
      if ([string]::IsNullOrWhiteSpace($content) -and $_.ErrorDetails -and $_.ErrorDetails.Message) { $content = $_.ErrorDetails.Message }
      return [PSCustomObject]@{ StatusCode = [int]$response.StatusCode; Headers = $response.Headers; Content = $content }
    }
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
  $backendArguments = if ($Transport -eq "stdio") { @("--stdio-backend", "true") } else { @() }
  $arguments = @("--server", $fakeServer, "--host", "127.0.0.1", "--port", [string]$port, "--log-dir", $testRoot, "--snapshot-dir", (Join-Path $testRoot "snapshots"), "--read-only", "true") + $backendArguments + @("--request-timeout-seconds", "10", "--hard-recovery-timeout-seconds", "20", "--max-queued-requests", "0")
  $process = Start-Process -FilePath $bridge -ArgumentList $arguments -PassThru -WindowStyle Hidden
  $url = "http://127.0.0.1:$port/mcp/"
  $headers = @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream" }
  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  do { Start-Sleep -Milliseconds 100; $health = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($url + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } -TimeoutSec 1 } }
  while ($health.StatusCode -ne 200 -and [DateTime]::UtcNow -lt $deadline)

  $initialize = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"streamable-verifier","version":"1"}}}'
  $init = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  if ($init.StatusCode -ne 200 -or $init.Headers["Content-Type"] -notmatch "application/json" -or $init.Content -notmatch 'fake-mcp') { throw "Initialize did not return a JSON MCP response." }
  $session = [string]$init.Headers["Mcp-Session-Id"]
  if ([string]::IsNullOrWhiteSpace($session)) { throw "Initialize did not establish an MCP session." }

  $corsPreflight = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Options -Headers @{ Origin = "http://127.0.0.1:$port"; "Access-Control-Request-Method" = "POST"; "Access-Control-Request-Headers" = "Mcp-Method, Mcp-Name" } }
  if ($corsPreflight.StatusCode -ne 204 -or $corsPreflight.Headers["Access-Control-Allow-Headers"] -notmatch "Mcp-Method" -or $corsPreflight.Headers["Access-Control-Allow-Headers"] -notmatch "Mcp-Name") { throw "CORS preflight did not allow the required 2026 MCP headers." }

  $otherInitialize = $initialize.Replace('"streamable-verifier"', '"zemax-mcp-launcher"')
  $secondClient = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $otherInitialize }
  if ([int]$secondClient.StatusCode -ne 409) { throw "A client could bypass the single-session bridge by claiming the launcher name." }

  $sseHeaders = @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "Mcp-Session-Id" = $session; "MCP-Protocol-Version" = "2025-03-26" }
  $list = '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  $sse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $sseHeaders -Body $list }
  $progressIndex = $sse.Content.IndexOf('notifications/progress')
  $resultIndex = $sse.Content.IndexOf('"result"')
  if ($sse.StatusCode -ne 200 -or $sse.Headers["Content-Type"] -notmatch "text/event-stream" -or $progressIndex -lt 0 -or $resultIndex -lt 0 -or $progressIndex -gt $resultIndex -or ([regex]::Matches($sse.Content, "event: message").Count -lt 2)) { throw "SSE MCP stream did not preserve notification-before-response ordering." }

  $unsupportedProtocol = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $session; "MCP-Protocol-Version" = "2025-06-18" } -Body $list }
  if ([int]$unsupportedProtocol.StatusCode -ne 400) { throw "An unsupported MCP-Protocol-Version was not rejected with HTTP 400." }

  $unsafeNotification = '{"jsonrpc":"2.0","method":"tools/call","params":{"name":"zemax_get_system","arguments":{}}}'
  $unsafeNotificationResponse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $session } -Body $unsafeNotification }
  if ([int]$unsafeNotificationResponse.StatusCode -ne 400) { throw "A non-notification JSON-RPC message without an id bypassed the request execution lock." }

  $serverRequest = '{"jsonrpc":"2.0","id":"server-request-parent","method":"test/server-request","params":{}}'
  $serverRequestJob = Start-Job -ScriptBlock {
    param($requestUrl, $token, $sessionId, $body)
    $response = Invoke-WebRequest -UseBasicParsing -Uri $requestUrl -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer $token"; Accept = "application/json, text/event-stream"; "Mcp-Session-Id" = $sessionId } -Body $body -TimeoutSec 10
    [PSCustomObject]@{ StatusCode = [int]$response.StatusCode; Content = $response.Content }
  } -ArgumentList $url, "streamable-verifier-token", $session, $serverRequest
  $serverRequestResponse = '{"jsonrpc":"2.0","id":"sampling-request-1","result":{"accepted":true}}'
  $serverRequestDeadline = [DateTime]::UtcNow.AddSeconds(5)
  do {
    $acceptedServerRequestResponse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $session } -Body $serverRequestResponse }
    if ([int]$acceptedServerRequestResponse.StatusCode -eq 409) { Start-Sleep -Milliseconds 100 }
  } while ([int]$acceptedServerRequestResponse.StatusCode -eq 409 -and [DateTime]::UtcNow -lt $serverRequestDeadline)
  if ([int]$acceptedServerRequestResponse.StatusCode -ne 202) { throw "The response to a server-initiated JSON-RPC request was not accepted while its parent request held the execution lock." }
  if (-not (Wait-Job -Job $serverRequestJob -Timeout 8)) { throw "Server request round trip deadlocked." }
  $serverRequestSse = Receive-Job -Job $serverRequestJob -ErrorAction Stop
  Remove-Job -Job $serverRequestJob -Force
  if ($serverRequestSse.StatusCode -ne 200 -or $serverRequestSse.Content -notmatch '"method":"sampling/createMessage"' -or $serverRequestSse.Content -notmatch '"completed":true') { throw "A server-initiated JSON-RPC request was not routed through its owning SSE response." }

  $serverRequestWithoutSse = '{"jsonrpc":"2.0","id":"server-request-no-sse","method":"test/server-request-no-sse","params":{}}'
  $withoutSse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $session } -Body $serverRequestWithoutSse }
  if ($withoutSse.StatusCode -ne 200 -or $withoutSse.Content -notmatch '"deliveryRejected":true') { throw "An undeliverable server request did not receive an immediate JSON-RPC error." }

  $badAccept = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/xml"; "Mcp-Session-Id" = $session } -Body $list }
  if ([int]$badAccept.StatusCode -ne 406) { throw "Unsupported Accept header was not rejected with HTTP 406." }

  $missingSession = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json" } -Body $list }
  if ([int]$missingSession.StatusCode -ne 400) { throw "A post-initialize request without Mcp-Session-Id bypassed session enforcement." }

  $notification = '{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}'
  $accepted = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $session } -Body $notification }
  if ($accepted.StatusCode -ne 202) { throw "MCP notification did not return HTTP 202." }

  $waitCancel = '{"jsonrpc":"2.0","id":"cancel-parent","method":"test/wait-cancel","params":{}}'
  $activeCancellation = '{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"cancel-parent"}}'
  $cancelRequest = [Net.HttpWebRequest]::Create($url)
  $cancelRequest.Method = "POST"
  $cancelRequest.ContentType = "application/json"
  $cancelRequest.Accept = "application/json, text/event-stream"
  $cancelRequest.Timeout = 10000
  $cancelRequest.Headers["Authorization"] = "Bearer streamable-verifier-token"
  $cancelRequest.Headers["Mcp-Session-Id"] = $session
  $cancelBytes = [Text.Encoding]::UTF8.GetBytes($waitCancel)
  $cancelRequestStream = $cancelRequest.GetRequestStream()
  try { $cancelRequestStream.Write($cancelBytes, 0, $cancelBytes.Length) }
  finally { $cancelRequestStream.Dispose() }
  $cancelResponse = [Net.HttpWebResponse]$cancelRequest.GetResponse()
  $cancelContent = ""
  $cancelSent = $false
  try {
    $cancelReader = [IO.StreamReader]::new($cancelResponse.GetResponseStream())
    try {
      while (($cancelLine = $cancelReader.ReadLine()) -ne $null) {
        if (-not $cancelLine.StartsWith("data: ")) { continue }
        $cancelContent += $cancelLine.Substring(6)
        if (-not $cancelSent -and $cancelLine -match 'ready-cancel') {
          $cancelDuringExecution = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $session } -Body $activeCancellation }
          if ($cancelDuringExecution.StatusCode -ne 202) { throw "Cancellation notification was blocked behind the active MCP request." }
          $cancelSent = $true
        }
      }
    } finally { $cancelReader.Dispose() }
  } finally { $cancelResponse.Dispose() }
  if (-not $cancelSent -or $cancelContent -notmatch '"cancelled":true') { throw "Cancellation notification did not complete the active MCP request." }

  $deleted = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Delete -Headers @{ Authorization = "Bearer streamable-verifier-token"; "Mcp-Session-Id" = $session } }
  if ($deleted.StatusCode -ne 200) { throw "Session DELETE did not return HTTP 200." }
  $afterDelete = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "Mcp-Session-Id" = $session } -Body $list }
  if ([int]$afterDelete.StatusCode -ne 404) { throw "Deleted MCP session was accepted." }

  $legacyInitialize = $initialize.Replace('2025-03-26', '2025-11-25')
  $legacyInit = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $legacyInitialize }
  $legacySession = [string]$legacyInit.Headers["Mcp-Session-Id"]
  if ($legacyInit.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($legacySession) -or $legacyInit.Content -notmatch '"protocolVersion":"2025-11-25"') { throw "The bridge did not negotiate legacy MCP 2025-11-25." }
  $legacyList = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $legacySession; "MCP-Protocol-Version" = "2025-11-25" } -Body $list }
  if ($legacyList.StatusCode -ne 200) { throw "A 2025-11-25 request was rejected after successful initialization." }
  $legacyDeleted = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Delete -Headers @{ Authorization = "Bearer streamable-verifier-token"; "Mcp-Session-Id" = $legacySession; "MCP-Protocol-Version" = "2025-11-25" } }
  if ($legacyDeleted.StatusCode -ne 200) { throw "The 2025-11-25 session could not release its OpticStudio lease." }

  $unsupportedLegacyInitialize = $initialize.Replace('2025-03-26', '2025-06-18')
  $unsupportedLegacyInit = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $unsupportedLegacyInitialize }
  if ([int]$unsupportedLegacyInit.StatusCode -ne 400 -or $unsupportedLegacyInit.Content -notmatch '"code":-32022' -or $unsupportedLegacyInit.Content -notmatch '"id":1') { throw "An unsupported legacy initialize version was not rejected before Host session registration. status=$($unsupportedLegacyInit.StatusCode), content=$($unsupportedLegacyInit.Content)" }
  $mismatchedProtocolInitialize = $initialize.Replace('"streamable-verifier"', '"mismatched-protocol"')
  $mismatchedProtocolInit = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $mismatchedProtocolInitialize }
  if ([int]$mismatchedProtocolInit.StatusCode -ne 400 -or $mismatchedProtocolInit.Headers["Mcp-Session-Id"] -or $mismatchedProtocolInit.Content -notmatch '"code":-32022') { throw "A Worker protocol mismatch was committed as a legacy Host session." }

  $failedInitialize = $initialize.Replace('"streamable-verifier"', '"fail-init"')
  $failed = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $failedInitialize }
  if ($failed.StatusCode -ne 200 -or $failed.Headers["Mcp-Session-Id"] -or $failed.Content -notmatch 'simulated initialize failure') { throw "A failed initialize exposed a provisional MCP session." }
  $hangInitialize = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  $hangSession = [string]$hangInitialize.Headers["Mcp-Session-Id"]
  $hang = '{"jsonrpc":"2.0","id":3,"method":"test/hang","params":{}}'
  $timedOut = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers (@{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $hangSession }) -Body $hang -TimeoutSec 15 }
  if ([int]$timedOut.StatusCode -ne 504) { throw "Hung MCP request did not receive the soft timeout response." }
  $queueFull = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers (@{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $hangSession }) -Body $list }
  if ([int]$queueFull.StatusCode -ne 429) { throw "A request was accepted even though the configured MCP queue had no capacity." }
  $activeDelete = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Delete -Headers @{ Authorization = "Bearer streamable-verifier-token"; "Mcp-Session-Id" = $hangSession } }
  if ([int]$activeDelete.StatusCode -ne 409) { throw "The active MCP session was deleted while its shared OpticStudio operation was still draining." }
  $recoveryDeadline = [DateTime]::UtcNow.AddSeconds(18)
  do {
    Start-Sleep -Milliseconds 500
    $postRecoveryHealth = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($url + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } -TimeoutSec 5 }
    $postRecovery = $postRecoveryHealth.Content | ConvertFrom-Json
  } while (($postRecovery.hardRecoveryCount -lt 1 -or -not $postRecovery.mcpServerRunning) -and [DateTime]::UtcNow -lt $recoveryDeadline)
  if ($postRecovery.hardRecoveryCount -lt 1 -or -not $postRecovery.mcpServerRunning) { throw "Hard timeout did not terminate and recover the stuck stdio MCP server." }

  $pumpRestartBaseline = [int]$postRecovery.serverRestartCount
  $pumpInitialize = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  $pumpSession = [string]$pumpInitialize.Headers["Mcp-Session-Id"]
  if ([string]::IsNullOrWhiteSpace($pumpSession)) { throw "Could not initialize a client after hard recovery." }
  $duplicateServerRequest = '{"jsonrpc":"2.0","id":"duplicate-parent","method":"test/duplicate-server-request","params":{}}'
  $null = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "Mcp-Session-Id" = $pumpSession } -Body $duplicateServerRequest -TimeoutSec 10 }
  $pumpRecoveryDeadline = [DateTime]::UtcNow.AddSeconds(12)
  do {
    Start-Sleep -Milliseconds 500
    $pumpHealth = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($url + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } -TimeoutSec 5 }
    $afterPumpFailure = $pumpHealth.Content | ConvertFrom-Json
  } while (($afterPumpFailure.serverRestartCount -le $pumpRestartBaseline -or -not $afterPumpFailure.mcpServerRunning) -and [DateTime]::UtcNow -lt $pumpRecoveryDeadline)
  if ($afterPumpFailure.serverRestartCount -le $pumpRestartBaseline -or -not $afterPumpFailure.mcpServerRunning) { throw "A stdout pump failure did not terminate and recover the MCP stdio server." }
  $postPumpInitialize = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  $postPumpSession = [string]$postPumpInitialize.Headers["Mcp-Session-Id"]
  if ($postPumpInitialize.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($postPumpSession)) { throw "The recovered MCP server could not initialize a new client." }
  $initializedNotification = '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
  $initialized = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $postPumpSession } -Body $initializedNotification }
  if ($initialized.StatusCode -ne 202) { throw "The recovered MCP server did not accept notifications/initialized." }
  $postPumpList = '{"jsonrpc":"2.0","id":"post-pump-list","method":"tools/list","params":{}}'
  $postPumpTools = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json"; "Mcp-Session-Id" = $postPumpSession } -Body $postPumpList }
  if ($postPumpTools.StatusCode -ne 200 -or $postPumpTools.Content -notmatch '"result"') { throw "The recovered MCP server could not complete tools/list." }
  $releasedPostPump = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Delete -Headers @{ Authorization = "Bearer streamable-verifier-token"; "Mcp-Session-Id" = $postPumpSession } }
  if ($releasedPostPump.StatusCode -ne 200) { throw "The recovered legacy session could not release its OpticStudio control lease." }

  # 2026-07-28 is stateless: no initialize and no Mcp-Session-Id. Every POST
  # carries matching body/header metadata, while the Host independently holds
  # the exclusive OpticStudio control lease.
  $modernMeta = '"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"stateless-verifier","version":"1"},"io.modelcontextprotocol/clientCapabilities":{},"io.zemaxmcp/clientInstanceId":"stateless-instance-a"}'
  $discover = '{"jsonrpc":"2.0","id":"discover-1","method":"server/discover","params":{' + $modernMeta + '}}'
  $modernHeaders = @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "MCP-Protocol-Version" = "2026-07-28"; "Mcp-Method" = "server/discover" }
  $discoverResponse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $modernHeaders -Body $discover }
  if ($discoverResponse.StatusCode -ne 200 -or $discoverResponse.Headers["Mcp-Session-Id"] -or $discoverResponse.Content -notmatch '"supportedVersions":\["2026-07-28"\]') { throw "Stateless server/discover did not return the 2026 protocol contract." }
  $modernList = '{"jsonrpc":"2.0","id":"modern-list","method":"tools/list","params":{' + $modernMeta + '}}'
  $modernListHeaders = @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "MCP-Protocol-Version" = "2026-07-28"; "Mcp-Method" = "tools/list" }
  $modernListResponse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $modernListHeaders -Body $modernList }
  if ($modernListResponse.StatusCode -ne 200 -or $modernListResponse.Headers["Mcp-Session-Id"] -or $modernListResponse.Content -notmatch '"result"') { throw "A stateless tools/list request was not forwarded without an MCP session." }
  $badModernHeader = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers (@{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "MCP-Protocol-Version" = "2026-07-28"; "Mcp-Method" = "tools/call" }) -Body $modernList }
  if ([int]$badModernHeader.StatusCode -ne 400 -or $badModernHeader.Content -notmatch '"code":-32020' -or $badModernHeader.Content -notmatch '"id":"modern-list"') { throw "A mismatched 2026 Mcp-Method header was accepted or lost its JSON-RPC id." }
  $unknownModernMeta = $modernMeta.Replace('2026-07-28', '2026-12-01')
  $unknownModern = $modernList.Replace($modernMeta, $unknownModernMeta)
  $unknownModernResponse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers (@{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "MCP-Protocol-Version" = "2026-12-01"; "Mcp-Method" = "tools/list" }) -Body $unknownModern }
  if ([int]$unknownModernResponse.StatusCode -ne 400 -or $unknownModernResponse.Content -notmatch '"code":-32022' -or $unknownModernResponse.Content -notmatch '"id":"modern-list"') { throw "An unknown modern protocol version incorrectly fell through to the legacy transport." }
  $modernCall = '{"jsonrpc":"2.0","id":"modern-call","method":"tools/call","params":{"name":"zemax_tool_catalog","arguments":{},' + $modernMeta + '}}'
  $modernCallHeaders = @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "MCP-Protocol-Version" = "2026-07-28"; "Mcp-Method" = "tools/call"; "Mcp-Name" = "zemax_tool_catalog" }
  $modernCallResponse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $modernCallHeaders -Body $modernCall }
  if ($modernCallResponse.StatusCode -ne 200 -or $modernCallResponse.Headers["Mcp-Session-Id"] -or $modernCallResponse.Content -notmatch '"result"') { throw "A stateless tool call did not complete through the OpticStudio lease." }
  $otherModernMeta = $modernMeta.Replace('stateless-instance-a', 'stateless-instance-b')
  $otherModernCall = $modernCall.Replace($modernMeta, $otherModernMeta)
  $otherModernCallResponse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $url -Method Post -ContentType "application/json" -Headers $modernCallHeaders -Body $otherModernCall }
  if ([int]$otherModernCallResponse.StatusCode -ne 409) { throw "A same-name stateless client with a distinct explicit instance identity bypassed the OpticStudio control lease." }

  # Opening an SSE response can fail before request execution starts. Verify that
  # this leaves no permanent active operation/session deletion blocker.
  $failureProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $failureProbe.Start(); $failurePort = ([Net.IPEndPoint]$failureProbe.LocalEndpoint).Port; $failureProbe.Stop()
  $failureArgs = @("--server", $fakeServer, "--host", "127.0.0.1", "--port", [string]$failurePort, "--log-dir", (Join-Path $testRoot "sse-open-failure"), "--snapshot-dir", (Join-Path $testRoot "failure-snapshots"), "--read-only", "true") + $backendArguments + @("--test-fail-sse-open", "true", "--request-timeout-seconds", "10", "--hard-recovery-timeout-seconds", "20")
  $sseFailureProcess = Start-Process -FilePath $bridge -ArgumentList $failureArgs -PassThru -WindowStyle Hidden
  $failureUrl = "http://127.0.0.1:$failurePort/mcp/"
  $failureDeadline = [DateTime]::UtcNow.AddSeconds(10)
  do { Start-Sleep -Milliseconds 100; $failureHealth = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($failureUrl + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } -TimeoutSec 1 } }
  while ($failureHealth.StatusCode -ne 200 -and [DateTime]::UtcNow -lt $failureDeadline)
  $failureInit = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $failureUrl -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  $failureSession = [string]$failureInit.Headers["Mcp-Session-Id"]
  $failureSse = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $failureUrl -Method Post -ContentType "application/json" -Headers @{ Authorization = "Bearer streamable-verifier-token"; Accept = "application/json, text/event-stream"; "Mcp-Session-Id" = $failureSession } -Body $list }
  if ([int]$failureSse.StatusCode -ne 500) { throw "The injected SSE-open failure did not return a recoverable HTTP error." }
  $afterFailureHealth = (Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($failureUrl + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } }).Content | ConvertFrom-Json
  if ($afterFailureHealth.activeOperations.Count -ne 0) { throw "An SSE-open failure leaked a permanent active MCP operation." }

  # The Worker can return a successful initialize result immediately before the
  # HTTP client disappears. Verify that a failed response write rolls back the
  # provisional session and leaves the one-client slot available.
  $initializeFailureProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $initializeFailureProbe.Start(); $initializeFailurePort = ([Net.IPEndPoint]$initializeFailureProbe.LocalEndpoint).Port; $initializeFailureProbe.Stop()
  $initializeFailureArgs = @("--server", $fakeServer, "--host", "127.0.0.1", "--port", [string]$initializeFailurePort, "--log-dir", (Join-Path $testRoot "initialize-write-failure"), "--snapshot-dir", (Join-Path $testRoot "initialize-write-failure-snapshots"), "--read-only", "true") + $backendArguments + @("--test-fail-initialize-response-write", "true", "--request-timeout-seconds", "10", "--hard-recovery-timeout-seconds", "20")
  $initializeWriteFailureProcess = Start-Process -FilePath $bridge -ArgumentList $initializeFailureArgs -PassThru -WindowStyle Hidden
  $initializeFailureUrl = "http://127.0.0.1:$initializeFailurePort/mcp/"
  $initializeFailureDeadline = [DateTime]::UtcNow.AddSeconds(10)
  do { Start-Sleep -Milliseconds 100; $initializeFailureHealth = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($initializeFailureUrl + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } -TimeoutSec 1 } }
  while ($initializeFailureHealth.StatusCode -ne 200 -and [DateTime]::UtcNow -lt $initializeFailureDeadline)
  $failedDelivery = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $initializeFailureUrl -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  if ([int]$failedDelivery.StatusCode -ne 500) { throw "The injected initialize-response write failure did not return HTTP 500." }
  $afterFailedDeliveryHealth = (Get-Response { Invoke-WebRequest -UseBasicParsing -Uri ($initializeFailureUrl + "health") -Headers @{ Authorization = "Bearer streamable-verifier-token" } }).Content | ConvertFrom-Json
  if ($afterFailedDeliveryHealth.provisionalSessionCount -ne 0 -or $afterFailedDeliveryHealth.clientCount -ne 0) { throw "A failed initialize response write leaked a provisional MCP session." }
  $recoveredInitialize = Get-Response { Invoke-WebRequest -UseBasicParsing -Uri $initializeFailureUrl -Method Post -ContentType "application/json" -Headers $headers -Body $initialize }
  if ($recoveredInitialize.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace([string]$recoveredInitialize.Headers["Mcp-Session-Id"])) { throw "A rolled-back provisional initialize session still consumed the client slot." }
  Write-Host "Streamable HTTP ($Transport) legacy/session and 2026 stateless negotiation, control leases, provisional sessions, ordered/time-bounded SSE messages, server-request round trips, in-flight cancellation, bounded queueing, stdout-pump recovery, and SSE-open cleanup verified."
}
finally {
  $env:ZEMAX_MCP_TOKEN = $oldToken
  if ($initializeWriteFailureProcess -and -not $initializeWriteFailureProcess.HasExited) { Stop-Process -Id $initializeWriteFailureProcess.Id -Force -ErrorAction SilentlyContinue; $initializeWriteFailureProcess.WaitForExit(3000) | Out-Null }
  if ($sseFailureProcess -and -not $sseFailureProcess.HasExited) { Stop-Process -Id $sseFailureProcess.Id -Force -ErrorAction SilentlyContinue; $sseFailureProcess.WaitForExit(3000) | Out-Null }
  if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; $process.WaitForExit(3000) | Out-Null }
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
