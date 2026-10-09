param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell -STA.' }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"))
Add-Type -Path (Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Newtonsoft.Json.dll")
$tasksPreference = $assembly.GetType('ZemaxMCP.Launcher.OfficialTasksSettings')
$binding = [Reflection.BindingFlags]'Static,NonPublic'
$readPreference = $tasksPreference.GetMethod('IsEnabled', $binding)
$hostArgument = $tasksPreference.GetMethod('HostArgument', $binding)
foreach ($case in @(
    @{ Token=$null; Expected=$true },
    @{ Token=[Newtonsoft.Json.Linq.JToken]::Parse('true'); Expected=$true },
    @{ Token=[Newtonsoft.Json.Linq.JToken]::Parse('false'); Expected=$false }
)) {
    $arguments = New-Object object[] 1
    $arguments[0] = $case.Token
    if ($readPreference.Invoke($null, $arguments) -ne $case.Expected) { throw 'Official Tasks preference default/override failed.' }
    $expectedArgument = '--enable-official-tasks ' + $case.Expected.ToString().ToLowerInvariant()
    if ($hostArgument.Invoke($null, @($case.Expected)) -ne $expectedArgument) { throw 'Official Tasks startup argument failed.' }
}
Write-Output 'Official Tasks default-on, explicit disabled preference, and startup arguments passed.'
$settingsStore = $assembly.GetType('ZemaxMCP.Launcher.LauncherSettingsStore')
$saveSettings = $settingsStore.GetMethod('Save', $binding)
$loadSettings = $settingsStore.GetMethod('Load', $binding)
$settingsTestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-settings-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($settingsTestDirectory)
try {
    $settingsPath = Join-Path $settingsTestDirectory 'launcher-settings.json'
    $preferences = [Newtonsoft.Json.Linq.JObject]::Parse('{"shareOnLan":true,"enableOfficialTasks":false,"remoteEndpoint":"http://test.invalid/mcp","localTokenProtected":"opaque-preserved-token"}')
    $saveArguments = New-Object object[] 2
    $saveArguments[0] = [string]$settingsPath
    $saveArguments[1] = [Newtonsoft.Json.Linq.JObject]$preferences
    $loadArguments = New-Object object[] 1
    $loadArguments[0] = [string]$settingsPath
    $saveSettings.Invoke($null, $saveArguments)
    $saved = $loadSettings.Invoke($null, $loadArguments)
    if ($saved.ToString() -ne $preferences.ToString()) { throw 'Atomic preferences roundtrip failed.' }
    $saveSettings.Invoke($null, $saveArguments)
    if (-not (Test-Path ($settingsPath + '.bak'))) { throw 'Settings backup was not created.' }
    [IO.File]::WriteAllText($settingsPath, '{broken-json')
    $restored = $loadSettings.Invoke($null, $loadArguments)
    if ($restored.ToString() -ne $preferences.ToString()) { throw 'Backup preference recovery failed.' }
} finally {
    $resolvedSettingsTest = (Resolve-Path $settingsTestDirectory).Path
    if (-not $resolvedSettingsTest.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedSettingsTest) -notlike 'ZemaxMCP-settings-test-*') { throw 'Unsafe settings fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedSettingsTest -Recurse -Force
}
Write-Output 'Atomic Launcher settings roundtrip, saved LAN/Tasks/token preferences and backup recovery passed.'
$installer = [Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Installer\bin\$Configuration\net48\Install.exe"))
$installerType = $installer.GetType('ZemaxMCP.Installer.MainWindow')
$createIcon = $installerType.GetMethod('CreateShortcutIcon', $binding)
$copyInitial = $installerType.GetMethod('CopyInitialInstall', $binding)
$iconTestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-icon-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($iconTestDirectory)
try {
    $iconPath = Join-Path $iconTestDirectory 'ZemaxMCP.ico'
    [IO.File]::Copy((Join-Path $root 'src\ZemaxMCP.Launcher\Assets\ZemaxMCP.ico'), $iconPath)
    $iconArguments = New-Object object[] 1
    $iconArguments[0] = [string]$iconTestDirectory
    $first = [string]$createIcon.Invoke($null, $iconArguments)
    $stream = [IO.File]::Create($iconPath)
    try { [Drawing.SystemIcons]::Application.Save($stream) } finally { $stream.Dispose() }
    $second = [string]$createIcon.Invoke($null, $iconArguments)
    if ($first -eq $second -or -not (Test-Path $first) -or -not (Test-Path $second)) { throw 'Shortcut icon paths must change with icon content.' }
    if ((Get-FileHash $iconPath).Hash -ne (Get-FileHash $second).Hash) { throw 'Shortcut icon bytes do not match the current icon.' }
    $sourceDirectory = Join-Path $iconTestDirectory 'source'
    $targetDirectory = Join-Path $iconTestDirectory 'target'
    [void][IO.Directory]::CreateDirectory($sourceDirectory)
    [void][IO.Directory]::CreateDirectory($targetDirectory)
    [IO.File]::WriteAllText((Join-Path $sourceDirectory 'launcher-settings.json'), 'package-must-not-overwrite')
    [IO.File]::WriteAllText((Join-Path $targetDirectory 'launcher-settings.json'), 'saved-preferences')
    [IO.File]::WriteAllText((Join-Path $sourceDirectory 'Install.exe'), 'package-only')
    [IO.File]::WriteAllText((Join-Path $sourceDirectory 'Start-Zemax-MCP.exe'), 'launcher')
    $copyArguments = New-Object object[] 2
    $copyArguments[0] = [string]$sourceDirectory
    $copyArguments[1] = [string]$targetDirectory
    $copyInitial.Invoke($null, $copyArguments)
    if ([IO.File]::ReadAllText((Join-Path $targetDirectory 'launcher-settings.json')) -ne 'saved-preferences' -or
        (Test-Path (Join-Path $targetDirectory 'Install.exe')) -or
        -not (Test-Path (Join-Path $targetDirectory 'Start-Zemax-MCP.exe'))) { throw 'Initial GUI install violated preference/package-only rules.' }
} finally {
    $resolvedIconTest = (Resolve-Path $iconTestDirectory).Path
    if (-not $resolvedIconTest.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedIconTest) -notlike 'ZemaxMCP-icon-test-*') { throw 'Unsafe icon fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedIconTest -Recurse -Force
}
Write-Output 'Installer initial preference preservation and content-addressed shortcut icon tests passed.'
$menu = [Activator]::CreateInstance($assembly.GetType('ZemaxMCP.Launcher.RoundedTrayMenu'), $true)
try {
    foreach ($size in @([Drawing.Size]::new(180,140), [Drawing.Size]::new(260,200))) {
        $menu.AutoSize = $false
        $menu.Size = $size
        if ($null -eq $menu.Region) { throw 'Menu has no rounded window region.' }
        if ($menu.Region.IsVisible(0,0)) { throw 'Top-left corner was not clipped.' }
        if ($menu.Region.IsVisible($size.Width-1,$size.Height-1)) { throw 'Bottom-right corner was not clipped.' }
        if (-not $menu.Region.IsVisible($size.Width/2,$size.Height/2)) { throw 'Menu center was clipped.' }
    }
} finally { $menu.Dispose() }
# Test native composition on an invisible, empty window. Never instantiate MainWindow:
# that would create a tray icon and could start the user's MCP service.
$window = [Windows.Window]::new()
try {
    $interop = [Windows.Interop.WindowInteropHelper]::new($window)
    [void]$interop.EnsureHandle()
    $method = $assembly.GetType('ZemaxMCP.Launcher.WindowMaterial').GetMethod('Apply')
    foreach ($material in @('mica','acrylic','solid','mica','solid')) {
        $result = $method.Invoke($null, @($window, $material))
        if ([string]::IsNullOrWhiteSpace($result)) { throw 'Material result is empty.' }
        if ($material -eq 'solid' -and $window.Background.Color.A -ne 255) { throw 'Solid mode is not opaque.' }
        Write-Output "$material -> $result"
    }
} finally { $window.Close() }
Write-Output 'Launcher material and rounded menu smoke tests passed.'
& (Join-Path $PSScriptRoot 'test-launcher-task-presentation.ps1') -Configuration $Configuration
& (Join-Path $PSScriptRoot 'test-desktop-shortcuts.ps1') -Configuration $Configuration
& (Join-Path $PSScriptRoot 'test-launcher-layout.ps1')
