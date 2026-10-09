param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"))
Add-Type -Path (Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Newtonsoft.Json.dll")
$type = $assembly.GetType('ZemaxMCP.Launcher.MainWindow')
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$build = $type.GetMethod('BuildTaskHistory', $flags)
$jobs = [Newtonsoft.Json.Linq.JArray]::Parse('[{"jobId":"running","toolName":"zemax_pop","state":"Running","fraction":0.42,"elapsedSeconds":38,"message":"Propagating beam"},{"jobId":"queued","toolName":"zemax_pop","state":"Queued","queuePosition":2},{"jobId":"done","toolName":"zemax_pop","state":"Completed","fraction":1,"elapsedSeconds":7},{"jobId":"failed","toolName":"zemax_pop","state":"Failed","message":"Analysis failed"}]')
$health = [Newtonsoft.Json.Linq.JObject]::Parse('{"worker":{"workerGeneration":3},"tasks":[{"taskId":"task-1","jobId":"running","state":"working","createdAt":"2026-10-09T00:00:00Z"}]}')
$arguments = New-Object object[] 2
$arguments[0]=$jobs; $arguments[1]=$health
$items = $build.Invoke($null, $arguments)
if ($items.Count -ne 5) { throw 'Worker Jobs / owned Tasks did not reach the presentation model.' }
if (!$items[0].IsActive -or $items[0].ProgressFraction -ne 0.42 -or $items[0].Elapsed -ne '38s') { throw 'Reported progress/duration mapping failed.' }
if (!$items[1].IsActive -or $null -ne $items[1].ProgressFraction -or $items[1].Queue -notmatch '2') { throw 'Queued/unknown progress mapping failed.' }
if ($items[2].IsActive -or $items[3].IsActive -or $items[3].Message -ne 'Analysis failed') { throw 'Terminal task states are wrong.' }
if (!$items[4].IsOfficialTask -or !$items[4].IsActive -or $items[4].SelectionKey -eq $items[0].SelectionKey) { throw 'Task/Job selection identity or Working state failed.' }
if ($items[0].IsProgressIndeterminate -or !$items[1].IsProgressIndeterminate -or $items[2].IsProgressIndeterminate) { throw 'Intermediate/queued/completed progress presentation failed.' }
foreach ($endpoint in @(0,1)) {
    $jobs[0]['fraction'] = [Newtonsoft.Json.Linq.JValue]::new([double]$endpoint)
    $items = $build.Invoke($null, $arguments)
    if (!$items[0].IsProgressIndeterminate -or $items[0].ProgressHint -notmatch '38s' -or $items[0].DisplayText -match '\d+%') { throw 'Active endpoint-only progress must animate with elapsed time, not a misleading percentage.' }
}
$jobs[0]['fraction'] = [Newtonsoft.Json.Linq.JValue]::new([double]::NaN)
$items = $build.Invoke($null, $arguments)
if ($null -ne $items[0].ProgressFraction) { throw 'Non-finite progress must not become a determinate percentage.' }
if (!$items[0].IsProgressIndeterminate) { throw 'Unknown active progress must animate.' }
$xaml = [IO.File]::ReadAllText((Join-Path $root 'src\ZemaxMCP.Launcher\MainWindow.xaml'))
if ($xaml -notmatch 'Title="Zemax MCP"') { throw 'Window title must match the product name.' }
$parse = $type.GetMethod('SelectMcpSseResponse', $flags)
$sse = ': keepalive' + "`n`n" + 'data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":42}}' + "`n`n" + 'data: {"jsonrpc":"2.0","id":7,"result":{"ignored":true}}' + "`n`n" + 'data: {"jsonrpc":"2.0","id":2741,' + "`n" + 'data: "result":{"resultType":"task","taskId":"task-1"}}' + "`n`n"
$parseArguments = New-Object object[] 2
$parseArguments[0]=$sse; $parseArguments[1]=[Newtonsoft.Json.Linq.JValue]::new(2741)
$response = $parse.Invoke($null,$parseArguments)
if ($response['result']['taskId'].ToString() -ne 'task-1') { throw 'SSE progress/unrelated messages hid the final Task response.' }
$source = [IO.File]::ReadAllText((Join-Path $root 'src\ZemaxMCP.Launcher\MainWindow.xaml.cs'))
$probe = $source.Substring($source.IndexOf('private static string TestMcpFunctionality'))
$probe = $probe.Substring(0,$probe.IndexOf('private async void TasksPageGetTask_Click'))
if ($probe -match '"initialize"' -or $probe -notmatch '"server/discover"') { throw 'Modern capability probe regressed to legacy initialize.' }
Write-Output 'Task presentation: Running/Queued/Completed/Failed, honest progress, elapsed time, distinct Job/Task selection and SSE request-ID filtering passed.'
