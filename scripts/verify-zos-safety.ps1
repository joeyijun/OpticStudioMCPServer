param(
  [string]$Configuration = "Release",
  [string]$ZemaxRoot = $env:ZEMAX_ROOT
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$corePath = Join-Path $root "src\ZemaxMCP.Core\bin\$Configuration\net48\ZemaxMCP.Core.dll"
if (-not (Test-Path -LiteralPath $corePath)) { throw "Build ZemaxMCP.Core before running the ZOS safety verifier." }
if ([string]::IsNullOrWhiteSpace($ZemaxRoot)) { throw "Pass -ZemaxRoot so the safety verifier can load ZOSAPI interfaces." }
[Reflection.Assembly]::LoadFrom((Join-Path $ZemaxRoot "ZOSAPI_Interfaces.dll")) | Out-Null
$core = [Reflection.Assembly]::LoadFrom($corePath)

$dispatcherType = $core.GetType("ZemaxMCP.Core.Session.ZosApiDispatcher", $true)
$dispatcher = [Activator]::CreateInstance($dispatcherType, $true)
try {
  if ($dispatcher.ApartmentState -ne [Threading.ApartmentState]::STA) { throw "The ZOS-API dispatcher thread is not STA." }
  $task = $dispatcherType.GetMethod("GetExecutingThreadIdAsync", [Reflection.BindingFlags]"NonPublic,Instance").Invoke($dispatcher, @())
  $threadId = $task.GetAwaiter().GetResult()
  if ($threadId -ne $dispatcher.ThreadId) { throw "ZOS-API work did not execute on the dedicated dispatcher thread." }
}
finally { $dispatcher.Dispose() }

$oldReadOnly = $env:ZEMAX_MCP_READ_ONLY
try {
  $env:ZEMAX_MCP_READ_ONLY = "1"
  $safetyType = $core.GetType("ZemaxMCP.Core.Session.ZemaxOperationSafety", $true)
  $safety = [Activator]::CreateInstance($safetyType, $true)
  $isMutating = $safetyType.GetMethod("IsMutating", [Reflection.BindingFlags]"NonPublic,Static")
  if (-not $isMutating.Invoke($null, @("SetSurface")) -or $isMutating.Invoke($null, @("GetSurface"))) {
    throw "Zemax operation safety classification failed."
  }
  try {
    $safetyType.GetMethod("BeforeOperation", [Reflection.BindingFlags]"Public,Instance").Invoke($safety, @($null, "SetSurface"))
    throw "Read-only mode allowed a mutating operation."
  }
  catch {
    $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
    if ($message -notmatch "Read-only mode blocked") { throw }
  }
}
finally { $env:ZEMAX_MCP_READ_ONLY = $oldReadOnly }

$sessionSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Core\Session\ZemaxSession.cs")
$safetySource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Core\Session\ZemaxOperationSafety.cs")
if ($sessionSource -notmatch '_safety\.BeforeOperation' -or $safetySource -notmatch 'CopySystem\(\)' -or $safetySource -notmatch 'copy\.SaveAs') {
  throw "Mutating session operations are not guarded by an independent CopySystem snapshot."
}
Write-Host "Dedicated STA dispatch, read-only blocking, mutation classification, and pre-change snapshot wiring verified."
