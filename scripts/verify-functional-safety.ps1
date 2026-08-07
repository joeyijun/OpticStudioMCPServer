[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$coreRoot = Join-Path $root "src\ZemaxMCP.Core"
$workerProgramPath = Join-Path $root "src\ZemaxMCP.Server\Program.cs"
$analysisRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Analysis"
$configRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Configuration"
$glassRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\GlassCatalog"

$coreSource = Get-ChildItem -LiteralPath $coreRoot -Recurse -Filter "*.cs" -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw } | Out-String
$workerProgram = Get-Content -LiteralPath $workerProgramPath -Raw
$analysisFiles = @(Get-ChildItem -LiteralPath $analysisRoot -Recurse -Filter "*.cs" -File)

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

# Stage D MCE mutations must be atomic where validation can make them so, and
# otherwise expose/check the underlying OpticStudio mutation result. Invalid
# operand types are validated before inserting a row; typed cell values preserve
# Double/Integer/String semantics and ConfigPickup source metadata.
$addConfigOperand = Get-Content -LiteralPath (Join-Path $configRoot "AddConfigurationOperandTool.cs") -Raw
if ($addConfigOperand.IndexOf('Enum.GetNames(typeof(MultiConfigOperandType))', [StringComparison]::Ordinal) -lt 0 -or
    $addConfigOperand.IndexOf('row = insertAt == 0 ? mce.AddOperand()', [StringComparison]::Ordinal) -lt 0 -or
    $addConfigOperand.IndexOf('Enum.GetNames(typeof(MultiConfigOperandType))', [StringComparison]::Ordinal) -gt $addConfigOperand.IndexOf('row = insertAt == 0 ? mce.AddOperand()', [StringComparison]::Ordinal) -or
    $addConfigOperand -notmatch '!row\.ChangeType\(parsedType\)' -or
    $addConfigOperand -notmatch 'rolledBack = mce\.RemoveOperandAt\(row\.OperandNumber\)' -or
    $addConfigOperand -notmatch 'if \(!rolledBack\)') {
    throw "zemax_add_configuration_operand must validate the named operand type before mutation, check ChangeType, and report rollback failure explicitly."
}

$setConfigValue = Get-Content -LiteralPath (Join-Path $configRoot "SetConfigurationOperandValueTool.cs") -Raw
$fixedTypeValidationIndex = $setConfigValue.IndexOf('ValidateFixedValueMatchesCellType(cell.DataType', [StringComparison]::Ordinal)
$makeFixedIndex = $setConfigValue.IndexOf('cell.MakeSolveFixed()', [StringComparison]::Ordinal)
if ($setConfigValue -match '\bdynamic\b' -or
    $setConfigValue -notmatch 'CellDataType\.Double' -or
    $setConfigValue -notmatch 'CellDataType\.Integer' -or
    $setConfigValue -notmatch 'CellDataType\.String' -or
    $setConfigValue -notmatch '_S_ConfigPickup' -or
    $setConfigValue -notmatch 'pickup\.Operand' -or
    $setConfigValue -notmatch 'SolveStatus\.Success' -or
    $setConfigValue -notmatch '!cell\.MakeSolveFixed\(\)' -or
    $setConfigValue -notmatch 'Fixed value parameters and pickupConfig are mutually exclusive' -or
    $fixedTypeValidationIndex -lt 0 -or $makeFixedIndex -lt 0 -or $fixedTypeValidationIndex -gt $makeFixedIndex) {
    throw "zemax_set_configuration_operand_value must preserve typed MCE values, validate fixed-value type before solve mutation, and use the checked ConfigPickup/fixed-solve contract."
}

$getConfigOperands = Get-Content -LiteralPath (Join-Path $configRoot "GetConfigurationOperandsTool.cs") -Raw
if ($getConfigOperands -match '\bdynamic\b' -or
    $getConfigOperands -notmatch 'CellDataType\.Double' -or
    $getConfigOperands -notmatch 'CellDataType\.Integer' -or
    $getConfigOperands -notmatch 'CellDataType\.String' -or
    $getConfigOperands -notmatch 'PickupOperand' -or
    $getConfigOperands -notmatch '_S_ConfigPickup') {
    throw "zemax_get_configuration_operands must return type-preserving MCE cell data and typed ConfigPickup metadata."
}

$setCurrentConfiguration = Get-Content -LiteralPath (Join-Path $configRoot "SetCurrentConfigurationTool.cs") -Raw
$deleteConfigurationOperand = Get-Content -LiteralPath (Join-Path $configRoot "DeleteConfigurationOperandTool.cs") -Raw
$setConfigurationCount = Get-Content -LiteralPath (Join-Path $configRoot "SetNumberOfConfigurationsTool.cs") -Raw
if ($setCurrentConfiguration -notmatch '!mce\.SetCurrentConfiguration\(' -or
    $deleteConfigurationOperand -notmatch '!mce\.RemoveOperandAt\(' -or
    $setConfigurationCount -notmatch '!mce\.AddConfiguration\(false\)' -or
    $setConfigurationCount -notmatch '!mce\.DeleteConfiguration\(') {
    throw "Reviewed MCE mutators must retain explicit checks of OpticStudio's boolean mutation results."
}

# Glass-catalog filtering/export must not silently use incomplete source sets,
# accept non-finite criteria, fabricate malformed AGF numeric values, escape the
# Zemax Glasscat directory, or violate overwrite=false after an earlier TOCTOU check.
$filterService = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Core\Services\GlassCatalog\GlassFilterService.cs") -Raw
$agfParser = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Core\Services\GlassCatalog\AgfFileParser.cs") -Raw
$catalogExport = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Core\Services\GlassCatalog\CatalogExportService.cs") -Raw
$filterTool = Get-Content -LiteralPath (Join-Path $glassRoot "FilterGlassesTool.cs") -Raw
$exportTool = Get-Content -LiteralPath (Join-Path $glassRoot "ExportGlassCatalogTool.cs") -Raw

if ($filterService -notmatch 'public static void Validate\(GlassFilterCriteria' -or
    $filterService -notmatch 'double\.IsNaN' -or
    $filterService -notmatch 'ValidateRange\(c\.NdMin, c\.NdMax' -or
    $filterService -notmatch 'MaxMeltFrequency\.Value > 5') {
    throw "Glass filter criteria must reject non-finite values, contradictory ranges, and invalid melt-frequency bounds."
}
if ($agfParser -notmatch 'Malformed AGF catalog' -or
    $agfParser -notmatch 'double\.IsNaN' -or
    $agfParser -notmatch 'FileNotFoundException' -or
    $agfParser -match 'double\.TryParse\([^\r\n]+out double result\);\s*return result') {
    throw "AGF parsing must fail explicitly on malformed provided numeric data instead of fabricating zero values."
}
if ($catalogExport -notmatch 'ValidateCatalogName' -or
    $catalogExport -notmatch 'target\.StartsWith\(prefix, StringComparison\.OrdinalIgnoreCase\)' -or
    $catalogExport -notmatch 'if \(overwrite\)' -or
    $catalogExport -notmatch 'File\.Replace\(tempPath, fullOutputPath, null\)' -or
    $catalogExport -notmatch 'File\.Move\(tempPath, fullOutputPath\)') {
    throw "Glass catalog export must confine catalogName to Glasscat and enforce overwrite through the final atomic move/replace path."
}
if ($exportTool -notmatch 'CatalogExportService\.Export\(filtered, outputPath, catalogName, overwrite\)') {
    throw "zemax_export_glass_catalog must pass the public overwrite contract to the final Core filesystem write."
}
foreach ($sourceTool in @($filterTool, $exportTool)) {
    if ($sourceTool -notmatch 'missing = requestedNames\.Where\(name => !availableCatalogs\.ContainsKey\(name\)\)' -or
        $sourceTool -notmatch 'missing\.Length > 0') {
        throw "Glass filtering/export must fail when any requested source catalog is missing."
    }
}

Write-Host "Functional safety guards passed: Worker owns ZOS initialization; read-only analyses avoid MFE/config/filesystem side effects; reviewed Stage A-D contracts are intact."
