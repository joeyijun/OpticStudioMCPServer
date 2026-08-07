[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$workerProject = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Server\ZemaxMCP.Server.csproj") -Raw
$bootstrap = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Server\BootstrapProgram.cs") -Raw
$compatibility = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Server\Compatibility\ZosApiRuntimeCompatibility.cs") -Raw
$publish = Get-Content -LiteralPath (Join-Path $root "scripts\publish-windows.ps1") -Raw
$snapshotSafety = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Core\Session\ZemaxOperationSafety.cs") -Raw
$multistart = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Server\Tools\Optimization\MultistartOptimizeTool.cs") -Raw
$matrixVerifier = Join-Path $root "scripts\verify-zosapi-compatibility.ps1"

if ($workerProject -notmatch '<PlatformTarget>x64</PlatformTarget>' -or
    $workerProject -notmatch '<Prefer32Bit>false</Prefer32Bit>') {
    throw "The ZOS-API Worker must remain explicitly x64 for old NetHelper/registry compatibility."
}

$validateIndex = $bootstrap.IndexOf('ZosApiRuntimeCompatibility.Validate', [StringComparison]::Ordinal)
$serverLoadIndex = $bootstrap.IndexOf('GetType("ZemaxMCP.Server.ServerApplication"', [StringComparison]::Ordinal)
if ($validateIndex -lt 0 -or $serverLoadIndex -lt 0 -or $validateIndex -gt $serverLoadIndex) {
    throw "Worker bootstrap must validate runtime ZOS-API versions before loading ServerApplication ZOS-API types."
}
if ($compatibility -notmatch 'runtimeVersion\.CompareTo\(buildVersion\) < 0' -or
    $compatibility -notmatch 'throw new NotSupportedException' -or
    $compatibility -notmatch 'ZOSAPI_BUILD_INFO\.txt') {
    throw "Packaged Worker must reject runtime ZOS-API assemblies older than its compile baseline."
}

if ($publish -notmatch 'ZEMAX_API_BASELINE_ROOT' -or
    $publish -notmatch 'runtime-must-not-be-older-than-build' -or
    $publish -notmatch 'Write-ZosApiBuildInfo' -or
    $publish -notmatch 'ZOSAPI_BUILD_INFO\.txt') {
    throw "Release packaging must build against an explicit oldest-supported ZOS-API baseline and record its version marker."
}

if ($snapshotSafety -notmatch 'cross-version interchange baseline' -or
    $snapshotSafety -notmatch 'before-.*\.zmx' -or
    $snapshotSafety -match 'fileName\s*=.*\.zos') {
    throw "Safety snapshots must use ZMX so OpticStudio versions before 21.3 are not blocked by the newer ZOS file format."
}
if ($multistart -notmatch 'string saveExtension = "\.zmx"' -or
    $multistart -notmatch 'ZOS was introduced in OpticStudio 21\.3') {
    throw "Unsaved multistart checkpoints must default to ZMX for pre-21.3 compatibility."
}
if (-not (Test-Path -LiteralPath $matrixVerifier)) {
    throw "The multi-install ZOS-API compile compatibility verifier is missing."
}

Write-Host "ZOS-API cross-version policy guards passed: x64 Worker, build/runtime baseline preflight, ZMX legacy safety, and multi-version compile verification are intact."
