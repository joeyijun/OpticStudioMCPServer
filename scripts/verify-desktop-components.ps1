param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$launcherExe = Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"
$installerExe = Join-Path $root "src\ZemaxMCP.Installer\bin\$Configuration\net48\Install.exe"
$proxyExe = Join-Path $root "src\ZemaxMCP.ClientProxy\bin\$Configuration\net48\ZemaxMCP.ClientProxy.exe"
$bridgeExe = Join-Path $root "src\ZemaxMCP.HttpBridge\bin\$Configuration\net48\ZemaxMCP.Host.exe"
$updaterExe = Join-Path $root "src\ZemaxMCP.Updater\bin\$Configuration\net48\ZemaxMCP.Updater.exe"
foreach ($path in $launcherExe, $installerExe, $proxyExe, $bridgeExe, $updaterExe) {
  if (-not (Test-Path -LiteralPath $path)) { throw "Build output is missing: $path" }
}

$serverProject = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\ZemaxMCP.Server.csproj")
$serverBootstrap = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\BootstrapProgram.cs")
if ($serverProject -notmatch '<StartupObject>ZemaxMCP\.Server\.BootstrapProgram</StartupObject>') {
  throw "The ZOS-API-safe server bootstrap is not configured as the executable entry point."
}
if ($serverBootstrap -match '(?m)^\s*using\s+ZOSAPI' -or
    $serverBootstrap -notmatch 'AssemblyResolve' -or
    $serverBootstrap -notmatch 'ServerApplication') {
  throw "The server bootstrap can bind ZOS-API too early or does not hand off to ServerApplication."
}
$launcherXaml = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Launcher\MainWindow.xaml")
if ($launcherXaml -match 'AiClientsList' -or
    $launcherXaml -notmatch '(?s)AiStateDot.*AI client setup.*AiState') {
  throw "AI connection state must be a compact indicator inside AI client setup, without the old full client list."
}
if ($launcherXaml -notmatch 'Choose folder…' -or $launcherXaml -match 'Connection details') {
  throw "The launcher must provide a manual OpticStudio folder fallback and a compact status overview."
}
if ($launcherXaml -match 'Green: ready' -or $launcherXaml -match 'Amber: waiting' -or $launcherXaml -match 'Red: unavailable') {
  throw "The redundant status-color legend must not be shown at the bottom of the launcher."
}
foreach ($layoutMarker in 'Width="980" Height="800"', 'Property="Height" Value="36"', 'Property="CornerRadius" Value="12"', 'Content="Start"', 'Content="Stop"', 'Test MCP', 'Copy diagnostics', 'Copy secure setup') {
  if ($launcherXaml -notmatch [regex]::Escape($layoutMarker)) { throw "The polished launcher layout is missing: $layoutMarker" }
}
if ($launcherXaml -match 'Service control' -or
    $launcherXaml -notmatch '(?s)Grid\.Row="3".*McpStateDot.*ZosStateDot.*Grid\.Column="4".*AiStateDot') {
  throw "Service controls must be in the header and MCP, ZOS-API, and AI status cards must share one row."
}
if ($launcherXaml -notmatch 'RemoteSetupDot' -or
    $launcherXaml -notmatch 'Content="Copy secure setup".*MinWidth="145"') {
  throw "The remote secure-setup card must communicate its saved endpoint and keep the copy action fully visible."
}
if ($launcherXaml -notmatch 'x:Name="ToolsetProfile"' -or
    $launcherXaml -notmatch '完整专家模式') {
  throw "The launcher must offer the task-oriented toolset run configurations."
}
if ($launcherXaml -notmatch '(?s)Status overview.*Logs.*Updates.*Copy diagnostics') {
  throw "Status maintenance actions must remain grouped in the status overview card."
}
foreach ($clientMarker in 'CodexConfigDot', 'ClaudeConfigDot', 'CursorConfigDot', 'KimiConfigDot', 'WorkBuddyConfigDot', 'VsCodeConfigDot') {
  if ($launcherXaml -notmatch [regex]::Escape($clientMarker)) { throw "The AI client menu is missing its configuration indicator: $clientMarker" }
}
$launcherCode = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Launcher\MainWindow.xaml.cs")
if ($launcherCode -notmatch 'Remote endpoint active:' -or
    $launcherCode -notmatch 'token protected for this Windows user') {
  throw "The remote secure-setup status must identify the selected endpoint and encrypted credential."
}
if ($launcherCode -match 'Task\.Run\(\(\) => GetHealth\(endpoint, McpToken\)\)' -or
    $launcherCode -match 'Task\.Run\(\(\) => TestMcp\(endpoint, McpToken\)\)') {
  throw "The launcher must capture the token on the WPF dispatcher before starting a background HTTP request."
}
if ($launcherCode -notmatch '(?s)AiConfigMenu_Click.*RefreshClientMenuIndicators\(\).*IsOpen = true' -or
    $launcherCode -notmatch '(?s)RefreshClientDashboard.*RefreshClientMenuIndicators\(clientStatuses\)') {
  throw "AI client menu indicators must refresh automatically and immediately before the menu opens."
}
$publishScript = Get-Content -Raw (Join-Path $root "scripts\publish-windows.ps1")
foreach ($forbiddenPattern in '"*.log"', '"*.pdb"', '"ZOSAPI*.dll"') {
  if ($publishScript -notmatch [regex]::Escape($forbiddenPattern)) {
    throw "The release script does not exclude $forbiddenPattern."
  }
}
$bridgeSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\Program.cs")
if ($bridgeSource -notmatch 'EnvironmentVariables\.Remove\("ZEMAX_MCP_TOKEN"\)') {
  throw "The ZOS-API subprocess must not inherit the HTTP access token."
}
if ($bridgeSource -notmatch 'NamedPipeClientStream' -or $bridgeSource -notmatch 'ToolsetPolicy') {
  throw "The Host must isolate the Worker behind a named pipe and enforce the selected toolset policy."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ZemaxMCP-desktop-test-" + [guid]::NewGuid().ToString("N"))
$programRoot = Join-Path $testRoot "ANSYS Inc\v261\Zemax OpticStudio"
$dataRoot = Join-Path $testRoot "redirected-data"
$oldProgramRoot = $env:ZEMAX_ROOT
$oldDataRoot = $env:ZEMAX_DATA_ROOT
$oldLicense = $env:ANSYSLMD_LICENSE_FILE
$oldCodexHome = $env:CODEX_HOME
$oldKimiHome = $env:KIMI_CODE_HOME
$oldMcpToken = $env:ZEMAX_MCP_TOKEN
$proxyJob = $null

try {
  New-Item -ItemType Directory -Force -Path $programRoot, (Join-Path $programRoot "ZOS-API\Libraries"), (Join-Path $dataRoot "Configs"), (Join-Path $dataRoot "License") | Out-Null
  $fixture = Join-Path $root "src\ZemaxMCP.Launcher\Assets\ZemaxMCP.ico"
  Copy-Item $fixture (Join-Path $programRoot "ZOSAPI.dll")
  Copy-Item $fixture (Join-Path $programRoot "ZOSAPI_Interfaces.dll")
  Copy-Item $fixture (Join-Path $programRoot "ZOS-API\Libraries\ZOSAPI_NetHelper.dll")
  Copy-Item $fixture (Join-Path $dataRoot "Configs\SNTLCONFIG.XML")

  $env:ZEMAX_ROOT = $programRoot
  $env:ZEMAX_DATA_ROOT = $dataRoot
  $env:ANSYSLMD_LICENSE_FILE = "configured-for-test"
  $env:CODEX_HOME = Join-Path $testRoot "codex-home"
  $env:KIMI_CODE_HOME = Join-Path $testRoot "kimi-home"
  $launcher = [Reflection.Assembly]::LoadFrom($launcherExe)
  if ($launcher.GetName().Version -lt [Version]"1.1.0.0") { throw "The v1.1 launcher assembly version was not set." }
  $parseReleaseVersion = $launcher.GetType("ZemaxMCP.Launcher.MainWindow", $true).GetMethod("ParseReleaseVersion", [Reflection.BindingFlags]"NonPublic,Static")
  if ($parseReleaseVersion.Invoke($null, @("v1.2.3-rc1")) -ne [Version]"1.2.3.0") { throw "Release-tag version parsing failed." }
  $installationType = $launcher.GetType("ZemaxMCP.Launcher.ZemaxInstallation", $true)
  $installation = $installationType.GetMethod("FindAll").Invoke($null, @()) | Where-Object Root -eq $programRoot
  if (-not $installation) { throw "Synthetic modern Ansys installation was not detected." }
  if (-not $installation.ApiFilesPresent) { throw "The complete synthetic ZOS-API set was not recognized." }
  $manualInstallation = $installationType.GetMethod("FromFolder").Invoke($null, [object[]]@([string]$programRoot))
  if (-not $manualInstallation -or $manualInstallation.DiscoverySource -ne "manually selected folder" -or -not $manualInstallation.ApiFilesPresent) { throw "A valid manually selected OpticStudio folder was not accepted." }
  if ($installationType.GetMethod("FromFolder").Invoke($null, [object[]]@([string]$dataRoot))) { throw "An invalid manually selected folder was accepted." }
  if ($installation.NetHelperPath -ne (Join-Path $programRoot "ZOS-API\Libraries\ZOSAPI_NetHelper.dll")) { throw "The nested ZOS-API NetHelper location was not selected." }
  if ($installation.DataDirectory -ne $dataRoot -or $installation.DataDirectorySource -ne "ZEMAX_DATA_ROOT environment variable") { throw "The configured Zemax data root was not selected." }
  if ($installation.LicenseEvidence -notmatch "environment configured" -or $installation.LicenseEvidence -notmatch "configuration found") { throw "License configuration evidence was not reported." }
  $discoveryType = $launcher.GetType("ZemaxMCP.Launcher.ZemaxDiscovery", $true)
  $extractVersion = $discoveryType.GetMethod("ExtractVersion", [Reflection.BindingFlags]"NonPublic,Static")
  if ($extractVersion.Invoke($null, @("C:\builds\2026\ANSYS Inc\v261")) -le $extractVersion.Invoke($null, @("C:\builds\2027\ANSYS Inc\v252"))) { throw "Ansys single-component version folders are not ordered newest-first." }
  if ($extractVersion.Invoke($null, @("OpticStudio 2025 R2")) -le $extractVersion.Invoke($null, @("OpticStudio 2025 R1"))) { throw "Named OpticStudio releases are not ordered newest-first." }

  $configurator = $launcher.GetType("ZemaxMCP.Launcher.Configurator", $true)
  $expectedEndpoint = "http://127.0.0.1:8000/mcp"
  $expectedToken = "desktop-verifier-token"
  New-Item -ItemType Directory -Force -Path $env:CODEX_HOME, $env:KIMI_CODE_HOME | Out-Null
  Set-Content -LiteralPath (Join-Path $env:CODEX_HOME "config.toml") -Value "[unrelated]`r`nkeep = true`r`n"
  Set-Content -LiteralPath (Join-Path $env:KIMI_CODE_HOME "mcp.json") -Value '{"keep":true,"mcpServers":{"other":{"url":"http://127.0.0.1:1/mcp"}}}'
  $stringPair = [type[]]@([string], [string])
  $configurator.GetMethod("ConfigureCodex", [Reflection.BindingFlags]"Public,Static", $null, $stringPair, $null).Invoke($null, @($expectedEndpoint, $expectedToken))
  $configurator.GetMethod("ConfigureKimi", [Reflection.BindingFlags]"Public,Static", $null, $stringPair, $null).Invoke($null, @($expectedEndpoint, $expectedToken))
  $statuses = $configurator.GetMethod("GetClientStatuses", [Reflection.BindingFlags]"Public,Static", $null, $stringPair, $null).Invoke($null, @($expectedEndpoint, $expectedToken))
  if (@($statuses).Count -ne 6 -or @($statuses | Where-Object { [string]::IsNullOrWhiteSpace($_.ConfigPath) }).Count -ne 0) { throw "AI client configuration paths are incomplete." }
  $codexStatus = $statuses | Where-Object Name -eq "Codex"
  $kimiStatus = $statuses | Where-Object Name -eq "Kimi Code"
  if (-not $codexStatus.Configured -or $codexStatus.ConfigPath -ne (Join-Path $env:CODEX_HOME "config.toml")) { throw "CODEX_HOME configuration detection failed." }
  if (-not $kimiStatus.Configured -or $kimiStatus.ConfigPath -ne (Join-Path $env:KIMI_CODE_HOME "mcp.json")) { throw "KIMI_CODE_HOME configuration detection failed." }
  if ((Get-Content -Raw (Join-Path $env:CODEX_HOME "config.toml")) -notmatch [regex]::Escape("Bearer $expectedToken") -or
      (Get-Content -Raw (Join-Path $env:KIMI_CODE_HOME "mcp.json") | ConvertFrom-Json).mcpServers.'zemax-mcp'.headers.Authorization -ne "Bearer $expectedToken") { throw "AI client authentication headers were not written." }
  $staleStatuses = $configurator.GetMethod("GetClientStatuses", [Reflection.BindingFlags]"Public,Static", $null, $stringPair, $null).Invoke($null, @("http://127.0.0.1:9000/mcp", $expectedToken))
  if (($staleStatuses | Where-Object Name -eq "Codex").Configured -or ($staleStatuses | Where-Object Name -eq "Kimi Code").Configured) { throw "A stale AI-client endpoint was incorrectly reported as configured." }
  if ((Get-Content -Raw (Join-Path $env:CODEX_HOME "config.toml")) -notmatch "\[unrelated\]" -or -not (Get-Content -Raw (Join-Path $env:KIMI_CODE_HOME "mcp.json") | ConvertFrom-Json).keep) { throw "Configuring Zemax removed an unrelated AI-client setting." }
  $claudeFixture = Join-Path $testRoot "claude\claude_desktop_config.json"
  New-Item -ItemType Directory -Force -Path (Split-Path $claudeFixture -Parent) | Out-Null
  Set-Content -LiteralPath $claudeFixture -Value '{"keep":true,"mcpServers":{"other":{"command":"other.exe"}}}'
  $proxyArguments = [object[]]@([string]$claudeFixture, [string]$proxyExe, [string]$expectedEndpoint, [string]$expectedToken)
  $configurator.GetMethod("ConfigureStdioProxyJson", [Reflection.BindingFlags]"NonPublic,Static").Invoke($null, $proxyArguments)
  $claudeResult = Get-Content -Raw $claudeFixture | ConvertFrom-Json
  if (-not $claudeResult.keep -or -not $claudeResult.mcpServers.other -or $claudeResult.mcpServers.'zemax-mcp'.command -ne $proxyExe) { throw "Claude Desktop proxy configuration did not preserve existing settings." }
  if ($claudeResult.mcpServers.'zemax-mcp'.env.ZEMAX_MCP_TOKEN -ne $expectedToken -or
      @($claudeResult.mcpServers.'zemax-mcp'.args) -contains "--token") { throw "Claude Desktop must pass the token through its process environment rather than command-line arguments." }

  Add-Type -AssemblyName System.Drawing
  foreach ($executable in $launcherExe, $installerExe) {
    $icon = [Drawing.Icon]::ExtractAssociatedIcon($executable)
    if (-not $icon) { throw "No embedded Windows icon was found in $executable" }
    $icon.Dispose()
  }

  $bridgeAssembly = [Reflection.Assembly]::LoadFrom($bridgeExe)
  $bridgeOptionsType = $bridgeAssembly.GetType("ZemaxMCP.HttpBridge.BridgeOptions", $true)
  $bridgeType = $bridgeAssembly.GetType("ZemaxMCP.HttpBridge.StdioMcpBridge", $true)
  $parseOptions = $bridgeOptionsType.GetMethod("Parse", [Reflection.BindingFlags]"Public,Static")
  $env:ZEMAX_MCP_TOKEN = $null
  try {
    $parseOptions.Invoke($null, [object[]]@(,[string[]]@("--host", "0.0.0.0"))) | Out-Null
    throw "LAN bridge options were accepted without an access token."
  }
  catch {
    $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
    if ($message -notmatch "requires ZEMAX_MCP_TOKEN") { throw }
  }
  $env:ZEMAX_MCP_TOKEN = $expectedToken
  $securedOptions = $parseOptions.Invoke($null, [object[]]@(,[string[]]@("--host", "0.0.0.0", "--read-only", "true")))
  if ($securedOptions.AccessToken -ne $expectedToken -or -not $securedOptions.ReadOnly) { throw "Secured LAN bridge options were not retained." }
  $authorized = $bridgeType.GetMethod("IsAuthorized", [Reflection.BindingFlags]"NonPublic,Static")
  if (-not $authorized.Invoke($null, @("Bearer verifier-secret", "verifier-secret")) -or
      $authorized.Invoke($null, @("Bearer wrong", "verifier-secret")) -or
      $authorized.Invoke($null, @($null, "verifier-secret"))) { throw "Bearer-token authentication validation failed." }
  $originAllowed = $bridgeType.GetMethod("IsOriginAllowed", [Reflection.BindingFlags]"NonPublic,Static")
  $requestUri = [Uri]"http://192.168.8.1:8000/mcp"
  if ($originAllowed.Invoke($null, @("http://localhost:3000", $requestUri)) -or
      -not $originAllowed.Invoke($null, @("http://192.168.8.1:9000", $requestUri)) -or
      $originAllowed.Invoke($null, @("https://attacker.example", $requestUri))) { throw "MCP Origin validation failed." }
  $bridgeOptions = [Activator]::CreateInstance($bridgeOptionsType, $true)
  $bridge = [Activator]::CreateInstance($bridgeType, [Reflection.BindingFlags]"Instance,Public,NonPublic", $null, [object[]]@($bridgeOptions), $null)
  try {
    $handleStatus = $bridgeType.GetMethod("HandleServerStatus", [Reflection.BindingFlags]"Instance,NonPublic")
    foreach ($marker in @(
      "ZEMAX_MCP_STATUS:ZOS_API_LOADED",
      "ZEMAX_MCP_STATUS:ZOSAPI_ASSEMBLY:C:\Program Files\Zemax\ZOSAPI.dll",
      "ZEMAX_MCP_STATUS:ZOSAPI_INTERFACES_ASSEMBLY:C:\Program Files\Zemax\ZOSAPI_Interfaces.dll",
      "ZEMAX_MCP_STATUS:ZOSAPI_NETHELPER_ASSEMBLY:C:\Program Files\Zemax\ZOS-API\Libraries\ZOSAPI_NetHelper.dll",
      "ZEMAX_MCP_STATUS:ZOS_LICENSE_VALID:Premium",
      "ZEMAX_MCP_STATUS:ZEMAX_DATA_DIR:D:\Optics\Zemax",
      "ZEMAX_MCP_STATUS:SNAPSHOT_CREATED:D:\Optics\Snapshots\before-change.zos"
    )) { $handleStatus.Invoke($bridge, @($marker)) }
    $health = $bridgeType.GetMethod("BuildHealthPayload", [Reflection.BindingFlags]"Instance,NonPublic").Invoke($bridge, @()).ToString() | ConvertFrom-Json
    if (-not $health.zosApiLoaded -or $health.licenseStatus -ne "Valid — Premium" -or $health.zemaxDataDirectory -ne "D:\Optics\Zemax") { throw "Runtime ZOS-API/license/Data status markers were not reflected in bridge health." }
    if ($health.loadedZosApiFiles.zosApi -notlike "*ZOSAPI.dll" -or $health.loadedZosApiFiles.netHelper -notlike "*ZOSAPI_NetHelper.dll") { throw "Actually loaded ZOS-API assembly paths were not reported." }
    if ($health.lastSnapshotPath -notlike "*before-change.zos" -or -not $health.originValidationEnabled) { throw "Security/snapshot status was not reflected in bridge health." }
  }
  finally { $bridge.Dispose() }

  $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $portProbe.Start()
  $proxyTestPort = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
  $portProbe.Stop()
  $proxyJob = Start-Job -ArgumentList $proxyTestPort -ScriptBlock {
    param($port)
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$port/mcp/")
    $listener.Start()
    try {
      foreach ($index in 1, 2) {
        $context = $listener.GetContext()
        if ($context.Request.Headers["Authorization"] -ne "Bearer desktop-verifier-token") { throw "The proxy did not send the configured bearer token." }
        if ($index -eq 2 -and $context.Request.Headers["Mcp-Session-Id"] -ne "desktop-test-session") { throw "The proxy did not retain the MCP session." }
        $reader = [IO.StreamReader]::new($context.Request.InputStream, $context.Request.ContentEncoding)
        $request = $reader.ReadToEnd() | ConvertFrom-Json
        $reader.Dispose()
        $payload = @{ jsonrpc = "2.0"; id = $request.id; result = @{ ok = $true } } | ConvertTo-Json -Compress -Depth 4
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        if ($index -eq 1) { $context.Response.Headers["Mcp-Session-Id"] = "desktop-test-session" }
        $context.Response.ContentType = "application/json"
        $context.Response.ContentLength64 = $bytes.Length
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
      }
    }
    finally { $listener.Stop() }
  }
  Start-Sleep -Milliseconds 500
  $requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"desktop-test","version":"1.0"},"capabilities":{}}}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  )
  $oldProxyToken = $env:ZEMAX_MCP_TOKEN
  $env:ZEMAX_MCP_TOKEN = $expectedToken
  try { $responses = @($requests | & $proxyExe --url "http://127.0.0.1:$proxyTestPort/mcp/") }
  finally { $env:ZEMAX_MCP_TOKEN = $oldProxyToken }
  if ($LASTEXITCODE -ne 0 -or $responses.Count -ne 2 -or ($responses[1] | ConvertFrom-Json).id -ne 2) { throw "The Claude Desktop stdio-to-HTTP proxy relay failed." }
  Wait-Job $proxyJob -Timeout 10 | Out-Null
  Receive-Job $proxyJob -ErrorAction Stop | Out-Null
}
finally {
  $env:ZEMAX_ROOT = $oldProgramRoot
  $env:ZEMAX_DATA_ROOT = $oldDataRoot
  $env:ANSYSLMD_LICENSE_FILE = $oldLicense
  $env:CODEX_HOME = $oldCodexHome
  $env:KIMI_CODE_HOME = $oldKimiHome
  $env:ZEMAX_MCP_TOKEN = $oldMcpToken
  if ($proxyJob) { Stop-Job $proxyJob -ErrorAction SilentlyContinue; Remove-Job $proxyJob -Force -ErrorAction SilentlyContinue }
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

Write-Host "Desktop discovery, runtime health paths, AI config writes, client proxy relay/session, and embedded icons verified."
