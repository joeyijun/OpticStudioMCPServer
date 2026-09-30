param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell -STA.' }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"))
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
