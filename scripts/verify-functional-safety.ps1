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

Write-Host "Functional safety guards passed: Worker owns ZOS initialization, analysis MFE remains read-only, and reviewed Stage A/B/C contracts are intact."
