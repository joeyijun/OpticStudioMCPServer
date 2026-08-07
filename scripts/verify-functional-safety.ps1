[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$coreRoot = Join-Path $root "src\ZemaxMCP.Core"
$workerProgramPath = Join-Path $root "src\ZemaxMCP.Server\Program.cs"
$analysisRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Analysis"

$coreSource = Get-ChildItem -LiteralPath $coreRoot -Recurse -Filter "*.cs" -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw } | Out-String
$workerProgram = Get-Content -LiteralPath $workerProgramPath -Raw
$analysisFiles = @(Get-ChildItem -LiteralPath $analysisRoot -Recurse -Filter "*.cs" -File)
$analysisSource = $analysisFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw } | Out-String

# Global ZOS-API initialization is a Worker-process responsibility. Session
# reconnects must not repeat ZOSAPI_Initializer.Initialize and blur lifecycle
# ownership after the authenticated private-contract handshake.
if ($coreSource -match 'ZOSAPI_Initializer\.Initialize\s*\(') {
    throw "ZemaxMCP.Core must not initialize ZOS-API globally; Worker startup owns ZOSAPI_Initializer.Initialize."
}
if ($workerProgram -notmatch 'ZOSAPI_Initializer\.Initialize\s*\(') {
    throw "Worker startup must initialize ZOS-API after the Host/Worker contract handshake."
}

# Analysis tools are public ReadOnly operations. They may evaluate merit
# operands, but they must never mutate the user's Merit Function Editor simply
# to obtain a value. GetOperandValue is the side-effect-free ZOS-API path.
$forbiddenMfePatterns = @(
    '\.AddOperand\s*\(',
    '\.InsertNewOperandAt\s*\(',
    '\.RemoveOperandAt\s*\(',
    '\.RemoveOperandsAt\s*\(',
    '\.CalculateMeritFunction\s*\('
)
foreach ($pattern in $forbiddenMfePatterns) {
    $hits = @($analysisFiles | Select-String -Pattern $pattern)
    if ($hits.Count -gt 0) {
        $paths = @($hits | ForEach-Object { $_.Path } | Sort-Object -Unique)
        throw "Read-only analysis tools must not structurally mutate the Merit Function Editor. Pattern '$pattern' found in: $($paths -join ', ')"
    }
}

foreach ($requiredTool in @("SpotDiagramTool.cs", "RmsSpotTool.cs", "CardinalPointsTool.cs")) {
    $path = Join-Path $analysisRoot $requiredTool
    $source = Get-Content -LiteralPath $path -Raw
    if ($source -notmatch 'GetOperandValue\s*\(') {
        throw "$requiredTool must retain side-effect-free GetOperandValue-based merit operand evaluation."
    }
}

# These Stage A/B/C fixes are release-safety contracts rather than style. Keep
# explicit false/empty semantics, normalized ray bounds and cancellation wired.
$setSurface = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Server\Tools\LensData\SetSurfaceTool.cs") -Raw
if ($setSurface -notmatch 'material is not null' -or
    $setSurface -notmatch 'comment is not null' -or
    $setSurface -notmatch 'surface\.IsStop = isStop\.Value' -or
    $setSurface -notmatch 'MakeSolveFixed\(\)') {
    throw "zemax_set_surface must preserve omitted-vs-explicit-clear semantics."
}

foreach ($rayFile in @("RayTraceTool.cs", "RayTraceExtendedTool.cs")) {
    $source = Get-Content -LiteralPath (Join-Path $analysisRoot $rayFile) -Raw
    if ($source -notmatch 'ValidateNormalized' -or $source -notmatch 'CancellationToken cancellationToken') {
        throw "$rayFile must retain normalized-ray validation and cancellation support."
    }
}

# Geometric Image Analysis is classified ReadOnly. Persistent IMA.CFG writes
# and arbitrary filesystem exports therefore belong in zemax_export_analysis,
# not in this structured result tool. Strong typing also prevents silent
# reflection/property-name fallbacks from turning explicit requests into defaults.
$gia = Get-Content -LiteralPath (Join-Path $analysisRoot "GeometricImageAnalysisTool.cs") -Raw
if ($gia -notmatch 'IAS_GeometricImageAnalysis' -or
    $gia -notmatch 'if \(saveSettings\)' -or
    $gia -notmatch 'Use zemax_export_analysis' -or
    $gia -match '\.SaveTo\s*\(|File\.WriteAllBytes\s*\(|AnalysisBmpHelper\.TryExportBmp\s*\(') {
    throw "zemax_geometric_image_analysis must remain strongly typed and side-effect-free; persistent settings/files belong to zemax_export_analysis."
}

# The verified 2026 R1 IAS_GeometricEncircledEnergy contract has no
# ScaleByDiffractionLimit property. Keep the old public parameter only as a
# fail-explicit compatibility placeholder rather than pretending it was applied.
$gee = Get-Content -LiteralPath (Join-Path $analysisRoot "GeometricEncircledEnergyTool.cs") -Raw
if ($gee -notmatch 'if \(scaleByDiffractionLimit\)' -or
    $gee -notmatch 'NotSupportedException' -or
    $gee -match 'settings\.ScaleByDiffractionLimit') {
    throw "zemax_geometric_encircled_energy must reject the unsupported scaleByDiffractionLimit=true request explicitly."
}

# Fan parsers must not fabricate zeroes or claim success when a version-specific
# text layout is not understood. They also need cancellation at the session edge.
foreach ($fanFile in @("RayFanTool.cs", "OpticalPathFanTool.cs", "PupilAberrationFanTool.cs")) {
    $source = Get-Content -LiteralPath (Join-Path $analysisRoot $fanFile) -Raw
    if ($source -notmatch 'CancellationToken cancellationToken' -or
        $source -notmatch 'fields\.Count == 0' -or
        $source -notmatch 'FormatException') {
        throw "$fanFile must retain cancellation and fail-explicit text parsing."
    }
}

# Aperture throughput distinguishes a failed trace from an aperture/vignette
# loss. Cancellation is checked inside the potentially 10k-ray loop, and the
# clear fraction uses only successfully traced rays as its denominator.
$throughput = Get-Content -LiteralPath (Join-Path $analysisRoot "ApertureThroughputTool.cs") -Raw
if ($throughput -notmatch 'SuccessfulRays' -or
    $throughput -notmatch 'ClearFraction:\s*\(double\)clear / successful' -or
    $throughput -notmatch 'cancellationToken\.ThrowIfCancellationRequested\(\)' -or
    $throughput -notmatch 'ValidateNormalized') {
    throw "zemax_aperture_throughput must keep trace errors separate from aperture loss and remain cancellable."
}

Write-Host "Functional safety guards passed: Worker owns ZOS initialization; read-only analyses avoid MFE/config/filesystem side effects; reviewed Stage A/B/C contracts are intact."
