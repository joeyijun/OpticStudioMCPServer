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
foreach ($marker in 'AiStateDot', 'Choose folder', 'Content="Start"', 'Content="Stop"', 'Copy secure setup', 'x:Name="ToolsetProfile"') {
  if ($launcherXaml -notmatch [regex]::Escape($marker)) { throw "The desktop UI contract is missing: $marker" }
}
if ($launcherCode -notmatch '"Host", "ZemaxMCP\.Host\.exe"' -or
    $launcherCode -notmatch 'menu\.Items\.Add\("Start"' -or
    $launcherCode -notmatch 'menu\.Items\.Add\("Stop"' -or
    $launcherCode -notmatch 'menu\.Items\.Add\("Exit"') {
  throw "The launcher must locate the self-contained Host and retain tray controls."
}

$publish = Get-Content -Raw (Join-Path $root "scripts\publish-windows.ps1")
if ($publish -notmatch 'dotnet publish.*--self-contained true' -or
    $publish -notmatch 'Host\\ZemaxMCP\.Host\.exe' -or
    $publish -notmatch 'ZOSAPI\*\.dll') {
  throw "The portable package must contain a self-contained Host and no redistributed ZOS-API DLLs."
}
Write-Host "Desktop component and self-contained Host packaging verification passed."
