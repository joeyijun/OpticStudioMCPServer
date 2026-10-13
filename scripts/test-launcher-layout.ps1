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
if ($xaml -notmatch 'PreviewMouseWheel="RecentActivity_MouseWheel"' -or
    $xaml -match 'LostFocus="RemoteSecureSetup_LostFocus"') {throw 'Wheel routing or explicit setup import binding regressed.'}
$xaml = $xaml -replace '\s+x:Class="[^"]+"', '' -replace '\s+Icon="[^"]+"', ''
$xaml = $xaml -replace '\s+(Loaded|Click|Checked|Unchecked|SelectionChanged|TextChanged|PasswordChanged|LostFocus|PreviewMouseWheel)="[^"]+"', ''
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
 foreach ($name in @('EnableTls','TlsPfxPath','TlsPfxPassword')) {
  if ($owner.FindName($name)) {throw "Manual HTTPS control must not appear: $name"}
 }
 $owner.Content.SelectedIndex = 2
 Render $owner 'settings-automatic-https.png'
 $owner.Content.SelectedIndex = 0
 if ($owner.Width -ne 960 -or $owner.Height -ne 740) { throw 'Compact default window size regressed.' }
 $tabs = $owner.Content
 if ($tabs -isnot [System.Windows.Controls.TabControl] -or $tabs.Items.Count -ne 4) { throw 'Launcher must expose four focused pages.' }
 if (($tabs.Items | ForEach-Object {$_.Header}) -join ',' -ne 'Overview,Tasks,Settings,Diagnostics') { throw 'Page order regressed.' }
 if ($null -ne $owner.FindName('MaterialChoice')) { throw 'Material selection must not be exposed.' }
 if ($owner.FindName('TestConnectionButton').Content -ne 'Check connection') { throw 'Independent connectivity check is missing.' }
  if ($owner.FindName('TestMcpToolsButton').Content -ne 'Test MCP tools') { throw 'Deliberate MCP functional test button is missing.' }
 $tabs.SelectedIndex = 2
 Render $owner 'settings-page.png'
 $owner.Width = 860; $owner.Height = 620
 Render $owner 'settings-compact.png'
 $scroll = $tabs.Items[2].Content
 if ($scroll.ScrollableHeight -le 0) { throw 'Compact window must allow scrolling to lower content.' }
 $bar = $scroll.Template.FindName('PART_VerticalScrollBar', $scroll)
 if ($bar.Width -ne 12) { throw 'Slim scrollbar hit-target width regressed.' }
 $track = $bar.Template.FindName('PART_Track', $bar)
 if ($null -eq $track.Thumb -or $track.Thumb.MinHeight -lt 26) { throw 'Scrollbar has no usable drag thumb.' }
 $scroll.ScrollToVerticalOffset(70)
 $scroll.UpdateLayout()
 if ($scroll.VerticalOffset -le 0) { throw 'Slim scrollbar cannot scroll content.' }
 $owner.Width = 960; $owner.Height = 740
 $tabs.SelectedIndex = 3
 Render $owner 'diagnostics-page.png'
 $owner.FindName('CancelUpdateButton').Visibility='Visible'
 Render $owner 'diagnostics-update-cancel.png'
 $owner.FindName('CancelUpdateButton').Visibility='Collapsed'
 $tabs.SelectedIndex = 1
 $tabs.UpdateLayout()
 Render $owner 'tasks-page.png'
 if ($tabs.Items[1].Header -ne 'Tasks') { throw 'Independent Tasks tab not present.' }
 $taskScroll = $tabs.Items[1].Content
 if ($taskScroll -isnot [System.Windows.Controls.ScrollViewer]) { throw 'Tasks must remain scrollable in compact windows.' }
 if ($owner.FindName('TasksPageDetailsSection').IsExpanded) { throw 'Raw task details must be collapsed initially.' }
 if ($owner.FindName('TasksPageProgress').IsIndeterminate) { throw 'Idle tasks must not display animated progress.' }
 if ($owner.FindName('TasksPageCancel').IsEnabled) { throw 'Idle task cancellation must be disabled.' }
 foreach ($name in @('TasksPageSearch','TasksPageDetail')) {
   $control=$owner.FindName($name)
   $control.ApplyTemplate()
   $chrome=$control.Template.FindName('InputChrome',$control)
   if ($null -eq $chrome -or $chrome.CornerRadius.TopLeft -lt 6) {throw "Task input is not rounded: $name"}
 }
 $details=$owner.FindName('TasksPageDetailsSection')
 $details.IsExpanded=$true
 $details.ApplyTemplate()
 if ($details.Template.FindName('DisclosureBody',$details).Visibility -ne 'Visible') {throw 'Rounded task disclosure does not expand.'}
 if ($details.Template.FindName('DisclosureBody',$details).Padding.Top -lt 8) {throw 'Expanded details need breathing room below the header.'}
 $owner.FindName('TasksPageDetail').Text="Task result preview`n{`"success`":true}"
 $list=$owner.FindName('TasksPageJobs')
 $list.ItemsSource=@(
   [pscustomobject]@{ToolName='zemax_pop';ActivitySubtitle='MCP Task · 38s';State='working'},
   [pscustomobject]@{ToolName='zemax_nsc_ray_trace';ActivitySubtitle='Worker Job · queued';State='queued'}
 )
 $list.SelectedIndex=0
 Render $owner 'tasks-rounded-details.png'
 $details.IsExpanded=$false
 if ($details.Template.FindName('DisclosureBody',$details).Visibility -ne 'Collapsed') {throw 'Rounded task disclosure does not collapse.'}
 $tabs.SelectedIndex=2
 if ($owner.FindName('RemoteTrustSection')) {throw 'Duplicate certificate confirmation must not appear.'}
 if ($owner.FindName('ConnectRemoteButton').Content -ne 'Connect') {throw 'Remote setup requires an explicit Connect action.'}
 Render $owner 'remote-connection-setup.png'
 $tabs.SelectedIndex=1
 # Exercise the production wheel helper without starting a launcher/service.
 $route=$assembly.GetType('ZemaxMCP.Launcher.NestedScrollWheel').GetMethod('Route',[Reflection.BindingFlags]'Static,NonPublic')
 $outer=[Windows.Controls.ScrollViewer]::new()
 $outer.Content=[Windows.Controls.Border]@{Height=900;Width=200}
 $inner=[Windows.Controls.ScrollViewer]::new()
 $inner.Content=[Windows.Controls.Border]@{Height=400;Width=200}
 foreach($viewer in @($outer,$inner)) {
   $viewer.Measure([Windows.Size]::new(200,100)); $viewer.Arrange([Windows.Rect]::new(0,0,200,100)); $viewer.UpdateLayout()
 }
 function Wheel([int]$delta) {
   $event=[Windows.Input.MouseWheelEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice,0,$delta)
   $event.RoutedEvent=[Windows.Input.Mouse]::PreviewMouseWheelEvent
   $null=$route.Invoke($null,[object[]]@($inner,$outer,$event)); return $event
 }
 $inner.ScrollToVerticalOffset(50); $inner.UpdateLayout()
 if ((Wheel -120).Handled) {throw 'Scrollable activity must keep its own wheel inside the list.'}
 $inner.ScrollToBottom(); $inner.UpdateLayout()
 if (-not (Wheel -120).Handled) {throw 'Activity bottom must hand wheel to the page.'}
 $outer.UpdateLayout()
 if ($outer.VerticalOffset -le 0) {throw 'Activity bottom did not scroll the outer page.'}
 $inner.ScrollToTop(); $inner.UpdateLayout()
 if (-not (Wheel 120).Handled) {throw 'Activity top must hand wheel to the page.'}
 $inner.Content=[Windows.Controls.Border]@{Height=10;Width=200}; $inner.UpdateLayout()
 if (-not (Wheel -120).Handled) {throw 'Short or empty activity must not trap the page wheel.'}
 $taskPage = $taskScroll.Content
 $taskGrid = @($taskPage.Children | Where-Object { $_ -is [System.Windows.Controls.Grid] }) | Select-Object -First 1
 if ($null -eq $taskGrid -or $taskGrid.ColumnDefinitions.Count -ne 3) {
   throw 'Tasks must use a side-by-side activity list and selected-task details.'
 }
 if ($null -eq $owner.FindName('TasksPageSearch')) {
   throw 'Tasks text search is missing.'
 }

 $owner.FindName('TasksPageTitle').Text = 'NSC Ray Trace'
 $owner.FindName('TasksPageState').Text = 'Running'
 $owner.FindName('TasksPageMetadata').Text = 'Job demo-42 | client-a@1.0 | elapsed 35s'
 $owner.FindName('TasksPageProgress').Value = 62
 $owner.FindName('TasksPageProgressHint').Text = '62% reported'
 $owner.FindName('TasksPageMessage').Text = 'Tracing rays on the OpticStudio computer.'
 $owner.FindName('TasksPageCancel').IsEnabled = $true
 $owner.FindName('TasksPageViewResult').IsEnabled = $true
 $owner.FindName('TasksPageEmpty').Visibility = 'Collapsed'
 $owner.FindName('TasksPageSummary').Text = '1 active | 2 recent'
 $owner.FindName('TasksPageJobs').ItemsSource = @(
  [pscustomobject]@{ToolName='NSC Ray Trace';ActivitySubtitle='Worker Job | 35s | 62%';State='Running'},
  [pscustomobject]@{ToolName='FFT MTF';ActivitySubtitle='Worker Job | 4s';State='Completed'},
  [pscustomobject]@{ToolName='Official MCP Task';ActivitySubtitle='MCP Task | 12s';State='Cancelled'}
 )
 $owner.FindName('TasksPageJobs').SelectedIndex = 0
 Render $owner 'tasks-running.png'
 $owner.Width = 860; $owner.Height = 620
 Render $owner 'tasks-compact.png'
 foreach ($name in @('TasksPageCancel','TasksPageViewResult','TasksPageFilter','TasksPageSearch')) {
  $control = $owner.FindName($name)
  if ($control.ActualWidth -lt 60 -or $control.ActualHeight -lt 30) { throw "Task control clipped: $name" }
 }
 $owner.FindName('TasksPageProgress').IsIndeterminate = $true
 $owner.FindName('TasksPageProgressHint').Text = 'In progress | elapsed 35s | no intermediate estimate reported'
 $progress = $owner.FindName('TasksPageProgress')
 $progress.ApplyTemplate()
 $moving = $progress.Template.FindName('MovingIndicator',$progress)
 if ($moving.Visibility -ne 'Visible' -or $progress.Template.FindName('PART_Indicator',$progress).Visibility -ne 'Collapsed') { throw 'Indeterminate progress must show a moving segment, not a full percentage bar.' }
 Render $owner 'tasks-indeterminate.png'
 $owner.Width = 960; $owner.Height = 740
 $tabs.SelectedIndex = 0
 $tabs.UpdateLayout()
 $owner.Width = 860; $owner.Height = 620
 Render $owner 'overview-compact.png'
 foreach ($name in @('TestConnectionButton','AiConfigButton')) {
  $control = $owner.FindName($name)
  $point = $control.TranslatePoint([Windows.Point]::new(0,0),$owner.Content)
  if ($point.X -lt 0 -or $point.X + $control.ActualWidth -gt $owner.Width) { throw "Overview control exceeds window width: $name" }
 }
 $owner.Width = 960; $owner.Height = 740
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
 $arguments[1]='Cancel optical task?'
 $arguments[2]='Request cooperative cancellation? A running optical operation may take time to stop safely.'
 $arguments[3]='Cancel task'; $arguments[4]=$null
 $cancelDialog=$constructor.Invoke($arguments)
 try {Render $cancelDialog 'task-cancel-dialog.png'} finally {$cancelDialog.Close()}
 Write-Output 'Offline dashboard and client dialog render passed; no service was started.'
} finally {$owner.Close()}
