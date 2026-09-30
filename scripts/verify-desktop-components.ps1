param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$launcherExe = Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"
$installerExe = Join-Path $root "src\ZemaxMCP.Installer\bin\$Configuration\net48\Install.exe"
$proxyExe = Join-Path $root "src\ZemaxMCP.ClientProxy\bin\$Configuration\net48\ZemaxMCP.ClientProxy.exe"
$hostDll = Join-Path $root "src\ZemaxMCP.HttpBridge\bin\$Configuration\net10.0-windows\ZemaxMCP.Host.dll"
$updaterExe = Join-Path $root "src\ZemaxMCP.Updater\bin\$Configuration\net48\ZemaxMCP.Updater.exe"
foreach ($path in $launcherExe, $installerExe, $proxyExe, $hostDll, $updaterExe) {
  if (-not (Test-Path -LiteralPath $path)) { throw "Build output is missing: $path" }
}

$launcherXaml = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Launcher\MainWindow.xaml")
$launcherCode = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Launcher\MainWindow.xaml.cs")
$proxyCode = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.ClientProxy\Program.cs")
$installerCode = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Installer\MainWindow.xaml.cs")
$portableInstall = Get-Content -Raw (Join-Path $root "installer\Portable-Install.cmd")
$solution = Get-Content -Raw (Join-Path $root "OpticStudioMCPServer.sln")
$architectureDoc = Join-Path $root "docs\ARCHITECTURE.md"
$releaseWorkflow = Get-Content -Raw (Join-Path $root ".github\workflows\release.yml")
$publishReleaseWorkflow = Get-Content -Raw (Join-Path $root ".github\workflows\publish-release.yml")
foreach ($marker in 'AiStateDot', 'Content="Browse…"', 'Click="ChooseZemaxFolder_Click"', 'Content="Start"', 'Content="Stop"', 'Copy secure setup', 'x:Name="ToolsetProfile"') {
  if ($launcherXaml -notmatch [regex]::Escape($marker)) { throw "The desktop UI contract is missing: $marker" }
}
if ($launcherCode -notmatch '"Host", "ZemaxMCP\.Host\.exe"' -or
    $launcherCode -notmatch 'menu\.Items\.Add\("Start"' -or
    $launcherCode -notmatch 'menu\.Items\.Add\("Stop"' -or
    $launcherCode -notmatch 'menu\.Items\.Add\("Exit"') {
  throw "The launcher must locate the self-contained Host and retain tray controls."
}
if ($proxyCode -notmatch 'X-Zemax-MCP-Client-Instance' -or $proxyCode -notmatch 'Guid\.NewGuid') {
  throw "The packaged stdio proxy must emit a distinct per-process MCP client instance identity."
}
if ($installerCode -notmatch 'RunUpdater\(source, target\)' -or
    $installerCode -notmatch 'CopyInitialInstall' -or
    $portableInstall -notmatch 'ZemaxMCP\.Updater\.exe' -or
    $portableInstall -notmatch 'goto launch_installed' -or
    $portableInstall -notmatch 'portable mode') {
  throw "GUI and portable installation flows must reuse the updater for existing installs and make portable fallback explicit."
}
if ($solution -notmatch '= "ZemaxMCP\.Host", "src\\ZemaxMCP\.HttpBridge' -or
    $solution -notmatch '= "Runtime", "Runtime"' -or
    $solution -notmatch '= "Contracts", "Contracts"' -or
    $solution -notmatch '= "Desktop", "Desktop"' -or
    -not (Test-Path -LiteralPath $architectureDoc)) {
  throw "The solution/project organization must expose Host, Runtime, Contracts, Desktop, and the architecture ownership document."
}

$publish = Get-Content -Raw (Join-Path $root "scripts\publish-windows.ps1")
if ($publish -notmatch 'dotnet publish.*--self-contained true' -or
    $publish -notmatch 'Host\\ZemaxMCP\.Host\.exe' -or
    $publish -notmatch 'ZOSAPI\*\.dll') {
  throw "The portable package must contain a self-contained Host and no redistributed ZOS-API DLLs."
}
if ($releaseWorkflow -notmatch 'test-launcher-materials\.ps1' -or
    $publishReleaseWorkflow -notmatch 'actions:\s*read' -or
    $publishReleaseWorkflow -notmatch 'head_sha=\$sha' -or
    $publishReleaseWorkflow -notmatch 'Release signing is blocked') {
  throw "Release workflows must smoke-test the current launcher UI and refuse signing without successful CI at the exact tagged commit."
}
Write-Host "Desktop component, release-gate, client identity, project organization, and self-contained Host packaging verification passed."
