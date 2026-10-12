$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$session = Get-Content -Raw (Join-Path $root 'src/ZemaxMCP.Core/Session/ZemaxSession.cs')
$named = $session.Substring($session.IndexOf('public async Task<T> ExecuteAsync<T>('))
$guardIndex = $named.IndexOf('precondition?.Invoke')
$snapshotIndex = $named.IndexOf('_safety.BeforeOperation')
$writeIndex = $named.IndexOf('var result = operation(')
if ($guardIndex -lt 0 -or $guardIndex -ge $snapshotIndex -or $snapshotIndex -ge $writeIndex) {
    throw 'Model guard must run in the same STA callback before snapshot and operation.'
}
$batch = Get-Content -Raw (Join-Path $root 'src/ZemaxMCP.Server/Tools/LensData/BatchSetSurfacesTool.cs')
if ($batch -notmatch 'precondition: expectedBaselineId' -or
    $batch -notmatch 'EngineeringBaselineStore.Validate' -or
    $batch -notmatch 'EngineeringBaselineStore.RecordApplied') {
    throw 'Batch model guard or successful readback recording is missing.'
}
$rpc = Get-Content -Raw (Join-Path $root 'src/ZemaxMCP.Server/Rpc/WorkerRpcServer.cs')
$jobs = Get-Content -Raw (Join-Path $root 'src/ZemaxMCP.Server/Services/Jobs/McpJobManager.cs')
if ($rpc -notmatch 'structuredContent' -or $rpc -notmatch 'EngineeringObservation' -or
    $jobs -notmatch 'PublishResult\(entry, result, observation\)') {
    throw 'Foreground/background invocation provenance boundary regressed.'
}
Write-Output 'Engineering guards: atomic pre-snapshot model check and foreground/background provenance retained.'
