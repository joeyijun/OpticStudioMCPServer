[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$optimizationRoot = Join-Path $root "src\ZemaxMCP.Server\Tools\Optimization"
$constrainedRoot = Join-Path $root "src\ZemaxMCP.Core\Services\ConstrainedOptimization"

$forbes = Get-Content -LiteralPath (Join-Path $optimizationRoot "ForbesMeritFunctionTool.cs") -Raw
if ($forbes -match 'rings\s*=\s*Math\.Max|arms\s*=\s*Math\.Max' -or
    $forbes -notmatch 'ForbesPupilSampling\.RadauParameters\.ContainsKey\(rings\)' -or
    $forbes -notmatch 'SaveMeritFunction\(backupPath\)' -or
    $forbes -notmatch 'LoadMeritFunction\(backupPath\)' -or
    $forbes -notmatch 'if \(!mfe\.RemoveOperandAt\(row\)\)' -or
    $forbes -notmatch 'ChangeTypeChecked' -or
    $forbes -notmatch 'catch \(OperationCanceledException\)' -or
    $forbes -notmatch 'cancellationToken\.ThrowIfCancellationRequested\(\)' -or
    $forbes -notmatch 'bool useEqualFieldWeights = totalWeight <= 0' -or
    $forbes -notmatch 'useEqualFieldWeights \? 1\.0 / raw\.Count : item\.Weight / totalWeight') {
    throw "zemax_forbes_merit_function must retain strict sampling inputs, explicit Radau support, transactional MFE rollback, checked mutations, cancellation, and zero-weight-preserving field normalization."
}

$scanner = Get-Content -LiteralPath (Join-Path $constrainedRoot "VariableScanner.cs") -Raw
if ($scanner -notmatch 'GetOperandCell\(configuration\)' -or
    $scanner -match 'GetCellAt\(configuration\)' -or
    $scanner -notmatch 'CellDataType\.Double' -or
    $scanner -notmatch 'CancellationToken cancellationToken') {
    throw "VariableScanner must address MCE variables by configuration through GetOperandCell and preserve typed finite/cancellable scanning."
}

$accessor = Get-Content -LiteralPath (Join-Path $constrainedRoot "ZosVariableAccessor.cs") -Raw
if ($accessor -notmatch 'GetOperandCell\(variable\.ConfigColumn\)' -or
    $accessor -match 'GetCellAt\(variable\.ConfigColumn\)' -or
    $accessor -notmatch 'status != SolveStatus\.Success' -or
    $accessor -notmatch 'ApproximatelyEqual\(applied, value\)' -or
    $accessor -notmatch 'value == 0 \? 0 : 1\.0 / value') {
    throw "ZosVariableAccessor must use configuration-aware MCE cells, checked solve writes/readback, and preserve OpticStudio plane radius semantics."
}

$addOperand = Get-Content -LiteralPath (Join-Path $optimizationRoot "AddOperandTool.cs") -Raw
if ($addOperand -notmatch 'catch \(OperationCanceledException\)' -or
    $addOperand -notmatch 'if \(!row\.ChangeType\(parsedType\)\)' -or
    $addOperand -notmatch 'mfe\.RemoveOperandAt\(row\.OperandNumber\)' -or
    $addOperand -notmatch 'Merit Function became non-finite') {
    throw "zemax_add_operand must retain checked ChangeType, rollback, finite merit validation, and cancellation propagation."
}

$getVariables = Get-Content -LiteralPath (Join-Path $optimizationRoot "GetVariablesTool.cs") -Raw
if ($getVariables -notmatch 'CancellationToken cancellationToken' -or
    $getVariables -notmatch 'scanner\.ScanVariables\(system, cancellationToken\)' -or
    $getVariables -notmatch 'catch \(OperationCanceledException\)') {
    throw "zemax_get_variables must propagate cancellation through VariableScanner."
}

$constrainedOptimize = Get-Content -LiteralPath (Join-Path $optimizationRoot "ConstrainedOptimizeTool.cs") -Raw
if ($constrainedOptimize -notmatch 'scanner\.ScanVariables\(system, cancellationToken\)' -or
    $constrainedOptimize -notmatch 'catch \(OperationCanceledException\)') {
    throw "zemax_constrained_optimize must propagate cancellation through variable discovery and the optimizer."
}

$reader = Get-Content -LiteralPath (Join-Path $constrainedRoot "MeritFunctionReader.cs") -Raw
if ($reader -notmatch 'weight == 0' -or
    $reader -notmatch 'Weighted MFE row' -or
    $reader -notmatch 'throw new InvalidDataException' -or
    $reader -match 'weight > 0\s*&&\s*!double\.IsNaN') {
    throw "MeritFunctionReader must ignore only zero-weight rows and fail on invalid weighted merit data rather than silently dropping objectives."
}

$getMerit = Get-Content -LiteralPath (Join-Path $optimizationRoot "GetMeritFunctionTool.cs") -Raw
if ($getMerit -match '\.Sanitize\(' -or
    $getMerit -match 'catch\s*\{\s*\}' -or
    $getMerit -notmatch 'CellDataType\.Integer' -or
    $getMerit -notmatch 'CellDataType\.Double' -or
    $getMerit -notmatch 'CellDataType\.String' -or
    $getMerit -notmatch 'catch \(OperationCanceledException\)') {
    throw "zemax_get_merit_function must preserve typed cells, fail on non-finite data, and never fabricate zeroes through sanitize/empty catches."
}

Write-Host "Stage E optimization contract guards passed: MFE transactions, MCE variable addressing, typed reads, field weights, and cancellation are intact."
