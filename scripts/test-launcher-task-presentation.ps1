param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"))
Add-Type -Path (Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Newtonsoft.Json.dll")
$type = $assembly.GetType('ZemaxMCP.Launcher.MainWindow')
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$build = $type.GetMethod('BuildTaskHistory', $flags)
$jobs = [Newtonsoft.Json.Linq.JArray]::Parse('[{"jobId":"running","toolName":"zemax_pop","state":"Running","fraction":0.42,"elapsedSeconds":38,"message":"Propagating beam"},{"jobId":"queued","toolName":"zemax_pop","state":"Queued","queuePosition":2},{"jobId":"done","toolName":"zemax_pop","state":"Completed","fraction":1,"elapsedSeconds":7},{"jobId":"failed","toolName":"zemax_pop","state":"Failed","message":"Analysis failed"}]')
$health = [Newtonsoft.Json.Linq.JObject]::Parse('{"worker":{"workerGeneration":3},"tasks":[{"taskId":"task-1","jobId":"running","generation":3,"state":"working","createdAt":"2026-10-09T00:00:00Z"}]}')
$arguments = New-Object object[] 2
$arguments[0]=$jobs; $arguments[1]=$health
$items = $build.Invoke($null, $arguments)
if ($items.Count -ne 4) { throw 'A wrapped Worker Job must not appear again as a duplicate official Task.' }
if (!$items[0].IsActive -or $null -ne $items[0].ProgressFraction -or $items[0].Queue -notmatch '2') { throw 'Queued job must remain visible with unknown progress.' }
if ($items[1].IsActive -or $items[2].IsActive -or $items[2].Message -ne 'Analysis failed') { throw 'Terminal task states are wrong.' }
if (!$items[3].IsOfficialTask -or !$items[3].IsActive -or $items[3].JobId -ne 'running' -or
    $items[3].ToolName -ne 'zemax_pop' -or $items[3].ProgressFraction -ne 0.42 -or $items[3].Elapsed -ne '38s') {
    throw 'Linked owned MCP Task must inherit actual Worker Job progress and tool without duplicating it.'
}
if (!$items[0].IsProgressIndeterminate -or $items[1].IsProgressIndeterminate -or $items[3].IsProgressIndeterminate) {
    throw 'Indeterminate queue and determinate live progress were misclassified.'
}
foreach ($endpoint in @(0,1)) {
    $jobs[0]['fraction'] = [Newtonsoft.Json.Linq.JValue]::new([double]$endpoint)
    $items = $build.Invoke($null, $arguments)
    if (!$items[3].IsProgressIndeterminate -or $items[3].ProgressHint -notmatch '38s' -or
        $items[3].DisplayText -match '\d+%') {
        throw 'Active endpoint-only progress must animate with elapsed time, not a misleading percentage.'
    }
}
$jobs[0]['fraction'] = [Newtonsoft.Json.Linq.JValue]::new([double]::NaN)
$items = $build.Invoke($null, $arguments)
if ($null -ne $items[3].ProgressFraction -or !$items[3].IsProgressIndeterminate) {
    throw 'Non-finite progress must not become a determinate percentage.'
}
$health['tasks'][0]['state'] = [Newtonsoft.Json.Linq.JValue]::new('completed')
$items = $build.Invoke($null, $arguments)
if ($items.Count -ne 4 -or $items[3].IsActive -or $null -ne $items[3].ProgressFraction -or
    $items[3].IsProgressIndeterminate) {
    throw 'A completed Task must not inherit stale fractional progress from the linked Worker Job.'
}
$health['tasks'][0]['state'] = [Newtonsoft.Json.Linq.JValue]::new('working')
$health['tasks'][0]['generation'] = [Newtonsoft.Json.Linq.JValue]::new([long]4)
$items = $build.Invoke($null, $arguments)
if ($items.Count -ne 5 -or !$items[4].IsOfficialTask -or $items[4].WorkerGeneration -ne '4') {
    throw 'Restored Task from a different Worker generation must not absorb a current Job of the same ID.'
}
$health['tasks'][0]['generation'] = [Newtonsoft.Json.Linq.JValue]::new([long]3)
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
Write-Output 'Task presentation: linked same-generation Task/Job deduplication, honest progress, restart isolation, elapsed time and SSE request-ID filtering passed.'
