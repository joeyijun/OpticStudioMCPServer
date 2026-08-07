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
$optimizationRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Optimization"
$nscRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\NonSequential"
$toleranceRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Tolerancing"

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

# Analysis tools may evaluate merit operands, but must never mutate the user's
# Merit Function Editor simply to obtain a value. GetOperandValue is the
# side-effect-free ZOS-API path for the reviewed read-only calculations.
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
        throw "Analysis tools must not structurally mutate the Merit Function Editor. Pattern '$pattern' found in: $($paths -join ', ')"
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

$gia = Get-Content -LiteralPath (Join-Path $analysisRoot "GeometricImageAnalysisTool.cs") -Raw
if ($gia -notmatch 'IAS_GeometricImageAnalysis' -or
    $gia -notmatch 'if \(saveSettings\)' -or
    $gia -notmatch 'Use zemax_export_analysis' -or
    $gia -match '\.SaveTo\s*\(|File\.WriteAllBytes\s*\(|AnalysisBmpHelper\.TryExportBmp\s*\(') {
    throw "zemax_geometric_image_analysis must remain strongly typed and side-effect-free; persistent settings/files belong to zemax_export_analysis."
}

$gee = Get-Content -LiteralPath (Join-Path $analysisRoot "GeometricEncircledEnergyTool.cs") -Raw
if ($gee -notmatch 'if \(scaleByDiffractionLimit\)' -or
    $gee -notmatch 'NotSupportedException' -or
    $gee -match 'settings\.ScaleByDiffractionLimit') {
    throw "zemax_geometric_encircled_energy must reject the unsupported scaleByDiffractionLimit=true request explicitly."
}

foreach ($fanFile in @("RayFanTool.cs", "OpticalPathFanTool.cs", "PupilAberrationFanTool.cs")) {
    $source = Get-Content -LiteralPath (Join-Path $analysisRoot $fanFile) -Raw
    if ($source -notmatch 'CancellationToken cancellationToken' -or
        $source -notmatch 'fields\.Count == 0' -or
        $source -notmatch 'FormatException') {
        throw "$fanFile must retain cancellation and fail-explicit text parsing."
    }
}

$throughput = Get-Content -LiteralPath (Join-Path $analysisRoot "ApertureThroughputTool.cs") -Raw
if ($throughput -notmatch 'SuccessfulRays' -or
    $throughput -notmatch 'ClearFraction:\s*\(double\)clear / successful' -or
    $throughput -notmatch 'cancellationToken\.ThrowIfCancellationRequested\(\)' -or
    $throughput -notmatch 'ValidateNormalized') {
    throw "zemax_aperture_throughput must keep trace errors separate from aperture loss and remain cancellable."
}

# Stage D MCE mutations must validate before mutation and check OpticStudio's
# mutation results. Typed cell values preserve Double/Integer/String and pickup metadata.
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
# fabricate malformed AGF numeric values, escape Glasscat, or violate overwrite=false.
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

# Stage E: constrained state and MFE files must be transactional; native and
# custom optimization must propagate cancellation and drain ZOS-API tools.
$constraintStore = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Core\Services\ConstrainedOptimization\ConstraintStore.cs") -Raw
$setVariableConstraints = Get-Content -LiteralPath (Join-Path $optimizationRoot "SetVariableConstraintsTool.cs") -Raw
$saveMf = Get-Content -LiteralPath (Join-Path $optimizationRoot "SaveMeritFunctionFileTool.cs") -Raw
$loadMf = Get-Content -LiteralPath (Join-Path $optimizationRoot "LoadMeritFunctionFileTool.cs") -Raw
$localOptimize = Get-Content -LiteralPath (Join-Path $optimizationRoot "OptimizeTool.cs") -Raw
$globalOptimize = Get-Content -LiteralPath (Join-Path $optimizationRoot "GlobalSearchTool.cs") -Raw
$hammer = Get-Content -LiteralPath (Join-Path $optimizationRoot "HammerOptimizationTool.cs") -Raw
$multistart = Get-Content -LiteralPath (Join-Path $optimizationRoot "MultistartOptimizeTool.cs") -Raw

if ($constraintStore -notmatch 'ReplaceAll\(' -or
    $constraintStore -notmatch 'ExtractRequiredDoubleValue' -or
    $constraintStore -notmatch 'invalid finite numeric value' -or
    $constraintStore -notmatch 'File\.Replace\(tempPath, sidecarPath, null\)' -or
    $constraintStore -notmatch 'File\.Move\(tempPath, sidecarPath\)') {
    throw "ConstraintStore must strictly parse bounds, replace state only after validation, and atomically write sidecars."
}
if ($setVariableConstraints -notmatch 'var staged = _constraintStore\.GetAll\(\)' -or
    $setVariableConstraints -notmatch '_constraintStore\.ReplaceAll\(staged\)' -or
    $setVariableConstraints -notmatch '_constraintStore\.ReplaceAll\(previous\)' -or
    $setVariableConstraints -notmatch 'catch \(OperationCanceledException\)') {
    throw "zemax_set_variable_constraints must validate/stage the entire batch, rollback store state on persistence failure, and preserve cancellation."
}
if ($saveMf -notmatch 'SaveMeritFunction\(tempPath\)' -or
    $saveMf -notmatch 'overwrite' -or
    $saveMf -notmatch 'File\.Replace\(tempPath, fullPath, null\)' -or
    $saveMf -notmatch 'File\.Move\(tempPath, fullPath\)') {
    throw "zemax_save_merit_function_file must save through a temporary MF and enforce atomic overwrite/no-clobber semantics."
}
if ($loadMf -notmatch 'SaveMeritFunction\(backupPath\)' -or
    $loadMf -notmatch 'LoadMeritFunction\(backupPath\)' -or
    $loadMf -notmatch 'cancellationToken\.ThrowIfCancellationRequested\(\)' -or
    $loadMf -notmatch 'pre-operation safety snapshot') {
    throw "zemax_load_merit_function_file must preserve a full-MFE rollback path across load/calculate/cancellation failures."
}
if ($localOptimize -notmatch 'ParseCycles' -or
    $localOptimize -notmatch 'CancelAndDrain\(' -or
    $localOptimize -notmatch 'WaitWithTimeout\(0\.25\)' -or
    $localOptimize -match 'OptimizationCycles\.Infinite') {
    throw "zemax_optimize must keep strict finite cycle mapping and cancellable polling; Infinite must not leak back into the synchronous tool."
}
if ($globalOptimize -notmatch 'RunUntilCompletionTimeoutOrCancellation' -or
    $globalOptimize -notmatch 'CancelAndDrain\(tool, "Global Optimization"\)' -or
    $globalOptimize -notmatch 'return "TimedOut"' -or
    $globalOptimize -notmatch 'catch \(OperationCanceledException\)') {
    throw "zemax_global_search must cancel/drain on wall-clock timeout or caller cancellation before reading stable results."
}
if ($hammer -notmatch 'hammer\.AutomaticOptimization = automatic' -or
    $hammer -notmatch 'hammer\.TargetRunTimeM = targetRuntimeMinutes' -or
    $hammer -notmatch 'CancelAndDrain\(hammer\)' -or
    $hammer -notmatch 'return "TimedOut"') {
    throw "zemax_hammer must preserve official AutomaticOptimization/TargetRunTimeM settings and explicit timeout cancellation."
}
if ($multistart -notmatch 'SaveSystemCopy\(' -or
    $multistart -notmatch 'system\.CopySystem\(\)' -or
    $multistart -notmatch 'catch \(OperationCanceledException\)' -or
    $multistart -match 'system\.SaveAs\(savePath\)') {
    throw "zemax_multistart_optimize must preserve cancellation semantics and checkpoint through CopySystem instead of changing the active lens identity."
}

# Stage F POP is deliberately HighImpact because it can emit files and performs
# temporary LDE resampling. It must restore temporary state and use cancellable,
# strongly typed result retrieval. NSC/TDE reads must not swap or fabricate data.
$operationMetadata = Get-Content -LiteralPath (Join-Path $root "src\ZemaxMCP.Core\Session\ZemaxOperationMetadata.cs") -Raw
$pop = Get-Content -LiteralPath (Join-Path $analysisRoot "PopTool.cs") -Raw
$nscDetector = Get-Content -LiteralPath (Join-Path $nscRoot "GetNscDetectorTool.cs") -Raw
$nscObjects = Get-Content -LiteralPath (Join-Path $nscRoot "GetNscObjectsTool.cs") -Raw
$nscParameters = Get-Content -LiteralPath (Join-Path $nscRoot "GetNscObjectParametersTool.cs") -Raw
$tolerances = Get-Content -LiteralPath (Join-Path $toleranceRoot "GetTolerancesTool.cs") -Raw
$analysisExport = Get-Content -LiteralPath (Join-Path $analysisRoot "ExportAnalysisTool.cs") -Raw

if ([regex]::Matches($operationMetadata, '"Pop"').Count -ne 1 -or
    [regex]::Matches($operationMetadata, '"zemax_pop"').Count -ne 1 -or
    $operationMetadata -notmatch '"OptimizationWizard", "Optimize", "Pop", "QuickFocus"' -or
    $operationMetadata -notmatch '"zemax_optimize",\s*"zemax_pop",\s*"zemax_quick_focus"') {
    throw "POP must remain explicitly classified HighImpact exactly once at both command and MCP tool levels."
}
if ($pop -match '\bdynamic\b' -or
    $pop -match 'ApplyAndWaitForCompletion\(' -or
    $pop -notmatch 'analysis\.Terminate\(\)' -or
    $pop -notmatch 'RestoreTemporaryResampling' -or
    $pop -notmatch 'WriteGridBinAtomic' -or
    $pop -notmatch 'overwriteOutputFiles' -or
    $pop -notmatch 'results\.GetDataGrid\(0\)' -or
    $pop -notmatch 'var matrix = grid\.Values' -or
    $pop -notmatch '_ => throw new ArgumentOutOfRangeException') {
    throw "zemax_pop must remain cancellable/typed, restore temporary LDE state, reject invalid sampling, and atomically handle outputs."
}
if ($nscDetector -notmatch 'out var rows, out var columns' -or
    $nscDetector -notmatch 'expectedPixels = checked\(\(ulong\)rows \* columns\)' -or
    $nscDetector -notmatch 'CancellationToken cancellationToken') {
    throw "zemax_get_nsc_detector must preserve the ZOS-API Rows/Cols output order, size cross-check, and cancellation."
}
if ($nscObjects -notmatch 'startObject > numberOfObjects' -or
    $nscObjects -notmatch 'CancellationToken cancellationToken' -or
    $nscObjects -notmatch 'ValidateFinite\(') {
    throw "zemax_get_nsc_objects must reject out-of-range pagination, remain cancellable, and reject non-finite object coordinates."
}
if ($nscParameters -notmatch 'startParameter > names\.Length' -or
    $nscParameters -notmatch 'CellDataType\.Integer' -or
    $nscParameters -notmatch 'CellDataType\.Double' -or
    $nscParameters -notmatch 'CellDataType\.String' -or
    $nscParameters -notmatch 'Enum\.TryParse<ObjectColumn>' -or
    $nscParameters -notmatch 'CancellationToken cancellationToken') {
    throw "zemax_get_nsc_object_parameters must fail explicit pagination/column mismatches and preserve typed cell values with cancellation."
}
if ($tolerances -notmatch 'startRow > numberOfOperands' -or
    $tolerances -notmatch 'ReadUsedFinite' -or
    $tolerances -notmatch 'row\.IsParam1Used' -or
    $tolerances -notmatch 'row\.IsParam2Used' -or
    $tolerances -notmatch 'row\.IsParam3Used' -or
    $tolerances -notmatch 'CancellationToken cancellationToken') {
    throw "zemax_get_tolerances must preserve TDE used flags, reject non-finite used bounds, reject bad pagination, and remain cancellable."
}

# Generic analysis export is an explicit HighImpact filesystem boundary. It must
# use a fixed allowlist and requested-format truthfulness rather than silently
# accepting arbitrary enum/numeric IDs or falling back to a different extension.
if ($analysisExport -match 'Enum\.TryParse\(name' -or
    $analysisExport -match 'fallbackPath|analysis\.ToFile\(' -or
    $analysisExport -notmatch 'results\.GetTextFile\(tempTextPath\)' -or
    $analysisExport -notmatch 'if \(!created' -or
    $analysisExport -notmatch 'CommitTempFile' -or
    $analysisExport -notmatch 'analysis\.Terminate\(\)' -or
    $analysisExport -notmatch 'CancellationToken cancellationToken') {
    throw "zemax_export_analysis must retain an explicit allowlist, requested-format truthfulness, atomic outputs, and cancellable analysis cleanup."
}

Write-Host "Functional safety guards passed: Worker lifecycle and reviewed Stage A-F safety/data-integrity contracts are intact."
