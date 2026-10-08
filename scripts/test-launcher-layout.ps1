param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\layout-preview'))
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell -STA.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root 'src\ZemaxMCP.Launcher\bin\Release\net48\Start-Zemax-MCP.exe'
$assembly = [Reflection.Assembly]::LoadFrom($dll)
# Load only presentation XAML: no launcher constructor, startup, tray, settings or services.
$xaml = [IO.File]::ReadAllText((Join-Path $root 'src\ZemaxMCP.Launcher\MainWindow.xaml'))
$xaml = $xaml -replace '\s+x:Class="[^"]+"', '' -replace '\s+Icon="[^"]+"', ''
$xaml = $xaml -replace '\s+(Loaded|Click|Checked|Unchecked|SelectionChanged|TextChanged|PasswordChanged|LostFocus)="[^"]+"', ''
$owner = [Windows.Markup.XamlReader]::Parse($xaml)
function Render($window, [string]$name) {
 $visual = $window.Content
 $visual.Measure([Windows.Size]::new($window.Width, $(if($window.SizeToContent -eq 'Height'){1000}else{$window.Height})))
 $height = if($window.SizeToContent -eq 'Height') {$visual.DesiredSize.Height} else {$window.Height}
 $visual.Arrange([Windows.Rect]::new(0,0,$window.Width,$height))
 $visual.UpdateLayout()
 $image = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$window.Width,[int]$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $background = [Windows.Media.DrawingVisual]::new()
 $context = $background.RenderOpen()
 $context.DrawRectangle($window.TryFindResource('WorkspaceSurface'),$null,[Windows.Rect]::new(0,0,$window.Width,$height))
 $context.Close()
 $image.Render($background)
 $image.Render($visual)
 $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
 $stream = [IO.File]::Create((Join-Path $OutputDirectory $name))
 try {$encoder.Save($stream)} finally {$stream.Dispose()}
}
try {
 Render $owner 'dashboard.png'
 if ($owner.Width -ne 960 -or $owner.Height -ne 740) { throw 'Compact default window size regressed.' }
 $tabs = $owner.Content
 if ($tabs -isnot [System.Windows.Controls.TabControl] -or $tabs.Items.Count -lt 2) { throw 'Launcher must expose overview and an independent Tasks page.' }
 $scroll = $tabs.Items[0].Content
 if ($scroll.ScrollableHeight -le 0) { throw 'Compact window must allow scrolling to lower content.' }
 $bar = $scroll.Template.FindName('PART_VerticalScrollBar', $scroll)
 if ($bar.Width -ne 12) { throw 'Slim scrollbar hit-target width regressed.' }
 $track = $bar.Template.FindName('PART_Track', $bar)
 if ($null -eq $track.Thumb -or $track.Thumb.MinHeight -lt 26) { throw 'Scrollbar has no usable drag thumb.' }
 $scroll.ScrollToVerticalOffset(70)
 $scroll.UpdateLayout()
 if ($scroll.VerticalOffset -le 0) { throw 'Slim scrollbar cannot scroll content.' }
 $tabs.SelectedIndex = 1
 $tabs.UpdateLayout()
 Render $owner 'tasks-page.png'
 if ($tabs.Items[1].Header -ne 'Tasks') { throw 'Independent Tasks tab not present.' }
 $tabs.SelectedIndex = 0
 $tabs.UpdateLayout()
 [void]([Windows.Interop.WindowInteropHelper]::new($owner)).EnsureHandle()
 $flags = [Reflection.BindingFlags]'Instance,NonPublic'
 $constructor = $assembly.GetType('ZemaxMCP.Launcher.LauncherDialog').GetConstructors($flags)[0]
 $rows = [Collections.Generic.List[ValueTuple[string,string]]]::new()
 $rows.Add([ValueTuple[string,string]]::new('Codex','Configured'))
 $rows.Add([ValueTuple[string,string]]::new('Claude Desktop','Setup needed'))
 $rows.Add([ValueTuple[string,string]]::new('Google Antigravity','Setup needed'))
 $arguments = New-Object object[] 5
 $arguments[0]=$owner; $arguments[1]='Connect your AI clients'
 $arguments[2]='Configure the detected clients for this MCP endpoint. Other MCP entries will be kept. Restart each client after setup.'
 $arguments[3]='Configure clients'; $arguments[4]=$rows
 $dialog = $constructor.Invoke($arguments)
 try {
  Render $dialog 'clients-dialog.png'
  if ($dialog.Owner -ne $owner -or $dialog.ShowInTaskbar -or $dialog.ResizeMode -ne 'NoResize') { throw 'Dialog ownership regressed.' }
  if ($dialog.ActualHeight -gt $dialog.MaxHeight) { throw 'Dialog exceeds work-area height.' }
 } finally {$dialog.Close()}
 Write-Output 'Offline dashboard and client dialog render passed; no service was started.'
} finally {$owner.Close()}
