[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$analysisRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Analysis"
$nscRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\NonSequential"
$toleranceRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Tolerancing"

$bmp = Get-Content -LiteralPath (Join-Path $analysisRoot "AnalysisBmpHelper.cs") -Raw
if ($bmp -match 'catch\s*\{' -or
    $bmp -notmatch 'CancellationToken cancellationToken' -or
    $bmp -notmatch 'ValidateFinite\(' -or
    $bmp -notmatch 'FileMode\.CreateNew') {
    throw "AnalysisBmpHelper must distinguish no-grid from invalid data, remain cancellable, reject non-finite pixels, and write only to fresh temporary paths."
}

$export = Get-Content -LiteralPath (Join-Path $analysisRoot "ExportAnalysisTool.cs") -Raw
if ($export -notmatch 'AnalysisBmpHelper\.TryExportBmp\(results, tempImagePath, cancellationToken\)' -or
    $export -notmatch 'results\.GetTextFile\(tempTextPath\)' -or
    $export -notmatch 'CommitTempFile' -or
    $export -match 'fallbackPath|analysis\.ToFile\(') {
    throw "zemax_export_analysis must propagate cancellation into BMP rendering, check TXT export truthfully, use atomic commits, and never fall back to a different requested format."
}

$pop = Get-Content -LiteralPath (Join-Path $analysisRoot "PopTool.cs") -Raw
if ($pop -notmatch 'RestoreTemporaryResampling' -or
    $pop -notmatch 'analysis\.Terminate\(\)' -or
    $pop -notmatch 'results\.GetDataGrid\(0\)' -or
    $pop -notmatch 'overwriteOutputFiles' -or
    $pop -match '\bdynamic\b') {
    throw "zemax_pop must retain typed result retrieval, cancellable analysis termination, temporary LDE-state restoration, and explicit output overwrite policy."
}

$detector = Get-Content -LiteralPath (Join-Path $nscRoot "GetNscDetectorTool.cs") -Raw
if ($detector -notmatch 'out var rows, out var columns' -or
    $detector -notmatch 'expectedPixels = checked\(\(ulong\)rows \* columns\)' -or
    $detector -notmatch 'CancellationToken cancellationToken') {
    throw "zemax_get_nsc_detector must retain official Rows/Cols ordering, size cross-check, and cancellation."
}

$objects = Get-Content -LiteralPath (Join-Path $nscRoot "GetNscObjectsTool.cs") -Raw
$parameters = Get-Content -LiteralPath (Join-Path $nscRoot "GetNscObjectParametersTool.cs") -Raw
if ($objects -notmatch 'startObject > numberOfObjects' -or $objects -notmatch 'ValidateFinite\(' -or
    $parameters -notmatch 'GetObjectCell\(column\)' -or
    $parameters -notmatch 'CellDataType\.Integer' -or
    $parameters -notmatch 'CellDataType\.Double' -or
    $parameters -notmatch 'CellDataType\.String') {
    throw "NSC object readers must reject bad pagination/non-finite coordinates and preserve parameter cell data types."
}

$tolerances = Get-Content -LiteralPath (Join-Path $toleranceRoot "GetTolerancesTool.cs") -Raw
if ($tolerances -notmatch 'ReadUsedFinite' -or
    $tolerances -notmatch 'row\.IsParam1Used' -or
    $tolerances -notmatch 'startRow > numberOfOperands' -or
    $tolerances -notmatch 'CancellationToken cancellationToken') {
    throw "zemax_get_tolerances must retain used-field semantics, strict pagination/finite bounds, and cancellation."
}

Write-Host "Stage F specialized contract guards passed: POP, NSC, tolerancing, BMP rendering, and generic exports retain reviewed safety/data-integrity behavior."
