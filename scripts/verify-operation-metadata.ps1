$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$toolsRoot = Join-Path $root "src\ZemaxMCP.Server\Tools"
$metadataPath = Join-Path $root "src\ZemaxMCP.Core\Session\ZemaxOperationMetadata.cs"
$catalogPath = Join-Path $toolsRoot "Catalog\ToolCatalogTool.cs"
$toolsetPath = Join-Path $root "src\ZemaxMCP.Toolsets\ToolsetCatalog.cs"

$sourceFiles = @(Get-ChildItem -LiteralPath $toolsRoot -Recurse -Filter "*.cs" -File)
$sourceCommands = @($sourceFiles | Select-String -Pattern 'ExecuteAsync\("([^"]+)"' -AllMatches | ForEach-Object {
    foreach ($match in $_.Matches) { $match.Groups[1].Value }
} | Sort-Object -Unique)
$sourceTools = @($sourceFiles | Select-String -Pattern 'McpServerTool\s*\(\s*Name\s*=\s*"([^"]+)"' -AllMatches | ForEach-Object {
    foreach ($match in $_.Matches) { $match.Groups[1].Value }
} | Sort-Object -Unique)
$metadata = Get-Content -Raw $metadataPath
$metadataCommands = @([regex]::Matches($metadata, '"[A-Za-z][A-Za-z0-9]+"') | ForEach-Object {
    $_.Value.Trim('"')
} | Where-Object { $_ -notlike 'zemax_*' } | Sort-Object -Unique)
$metadataToolOccurrences = @([regex]::Matches($metadata, '"zemax_[^"]+"') | ForEach-Object {
    $_.Value.Trim('"')
})
$metadataTools = @($metadataToolOccurrences | Sort-Object -Unique)
$impactByTool = @{}
$currentImpact = $null
foreach ($line in ($metadata -split "`r?`n")) {
    if ($line -match 'new OperationPolicy\(ZemaxOperationImpact\.(ReadOnly|Caution|HighImpact)') { $currentImpact = $matches[1] }
    if ($null -ne $currentImpact) {
        foreach ($match in [regex]::Matches($line, '"(zemax_[^"]+)"')) { $impactByTool[$match.Groups[1].Value] = $currentImpact }
    }
}

$missingCommands = @($sourceCommands | Where-Object { $_ -notin $metadataCommands })
$missingTools = @($sourceTools | Where-Object { $_ -notin $metadataTools })
if ($missingCommands.Count -gt 0) { throw "Commands missing explicit safety metadata: $($missingCommands -join ', ')" }
if ($missingTools.Count -gt 0) { throw "MCP tools missing explicit safety metadata: $($missingTools -join ', ')" }
$duplicateRisks = @($metadataToolOccurrences | Group-Object | Where-Object { $_.Count -ne 1 } | ForEach-Object Name)
$unexpectedRisks = @($metadataTools | Where-Object { $_ -notin $sourceTools })
if ($duplicateRisks.Count -gt 0) { throw "MCP tools must have exactly one explicit risk level: $($duplicateRisks -join ', ')" }
if ($unexpectedRisks.Count -gt 0) { throw "Explicit safety metadata references unregistered MCP tools: $($unexpectedRisks -join ', ')" }
if ((Get-Content -Raw $catalogPath) -match 'StartsWith\("zemax_(set|add|delete|remove|clear|calculate)_') {
    throw "Tool catalog must use ZemaxOperationMetadata instead of name-prefix risk rules."
}
if ($metadata -match 'MutatingPrefixes') { throw "Safety metadata must not use command-name prefixes." }

$toolset = Get-Content -Raw $toolsetPath
$domainMatches = [regex]::Matches($toolset, '\["(zemax_[^"]+)"\]\s*=\s*"([^"]+)"')
$domainTools = @($domainMatches | ForEach-Object { $_.Groups[1].Value })
$domainCounts = @{}
foreach ($name in $domainTools) { $domainCounts[$name] = 1 + [int]($domainCounts[$name] ?? 0) }
$duplicateDomains = @($domainCounts.Keys | Where-Object { $domainCounts[$_] -ne 1 })
$missingDomains = @($sourceTools | Where-Object { $_ -notin $domainTools })
$unexpectedDomains = @($domainTools | Where-Object { $_ -notin $sourceTools } | Sort-Object -Unique)
$invalidDomains = @($domainMatches | Where-Object { $_.Groups[2].Value -notin @('system', 'sequential-editing', 'non-sequential', 'analysis', 'optimization', 'tolerance', 'polarization', 'files', 'administration') })
if ($missingDomains.Count -gt 0) { throw "MCP tools missing explicit domain metadata: $($missingDomains -join ', ')" }
if ($unexpectedDomains.Count -gt 0) { throw "Explicit domain metadata references unregistered tools: $($unexpectedDomains -join ', ')" }
if ($duplicateDomains.Count -gt 0) { throw "MCP tools must have exactly one explicit domain: $($duplicateDomains -join ', ')" }
if ($invalidDomains.Count -gt 0) { throw "Explicit domain metadata contains an unknown domain." }
$domainLookup = [regex]::Match($toolset, 'public static string GetDomainId\(string toolName\)(?<body>[\s\S]*?)\r?\n    }\r?\n\r?\n    public static Domain').Value
if ([string]::IsNullOrWhiteSpace($domainLookup) -or $domainLookup -match 'StartsWith\(|IndexOf\(|IsAnalysisTool|zemax_get_|zemax_set_') {
    throw "Tool domains must be explicit metadata and must not use tool-name inference or get_/set_ fallbacks."
}

$profileDomains = @{
    'basic-viewing' = @('system', 'sequential-editing', 'analysis', 'administration')
    'sequential-design' = @('system', 'sequential-editing', 'analysis', 'polarization', 'files', 'administration')
    'nonsequential-stray-light' = @('system', 'non-sequential', 'analysis', 'files', 'administration')
    'optimization-tolerance' = @('system', 'sequential-editing', 'analysis', 'optimization', 'tolerance', 'polarization', 'files', 'administration')
    'full-expert' = @('system', 'sequential-editing', 'non-sequential', 'analysis', 'optimization', 'tolerance', 'polarization', 'files', 'administration')
}
$profileCases = @{
    'basic-viewing' = 'BasicViewing'
    'sequential-design' = 'SequentialDesign'
    'nonsequential-stray-light' = 'NonSequentialStrayLight'
    'optimization-tolerance' = 'OptimizationTolerance'
}
$profiles = @{
    'basic-viewing' = @{ present = @('zemax_status', 'zemax_get_system', 'zemax_spot_diagram', 'zemax_tool_catalog'); absent = @('zemax_set_surface', 'zemax_connect', 'zemax_get_nsc_objects', 'zemax_optimize', 'zemax_open_file') }
    'sequential-design' = @{ present = @('zemax_set_surface', 'zemax_get_polarization', 'zemax_open_file'); absent = @('zemax_get_nsc_objects', 'zemax_optimize') }
    'nonsequential-stray-light' = @{ present = @('zemax_get_nsc_objects', 'zemax_export_analysis', 'zemax_status'); absent = @('zemax_set_surface', 'zemax_optimize') }
    'optimization-tolerance' = @{ present = @('zemax_optimize', 'zemax_get_tolerances', 'zemax_save_merit_function_file'); absent = @('zemax_get_nsc_objects') }
    'full-expert' = @{ present = @('zemax_set_surface', 'zemax_get_nsc_objects', 'zemax_optimize', 'zemax_open_file'); absent = @() }
}

if ($toolset -notmatch 'public static IEnumerable<string> EnabledImpacts' -or $toolset -notmatch 'ToolImpact\.ReadOnly') {
    throw "Toolset profiles must declare their allowed impacts as well as their domains."
}
if (-not [regex]::IsMatch($toolset, 'EnabledImpacts\(profile\)[\s\S]*?GetImpact\(toolName!?', [Text.RegularExpressions.RegexOptions]::Singleline)) {
    throw "Toolset admission must compose explicit domain and impact metadata."
}
$domainByTool = @{}
foreach ($match in $domainMatches) { $domainByTool[$match.Groups[1].Value] = $match.Groups[2].Value }
$readOnlyBlock = [regex]::Match($toolset, 'ReadOnlyTools\s*=\s*new HashSet<string>\(StringComparer\.Ordinal\)\s*\{(?<tools>[\s\S]*?)\n    \};').Groups['tools'].Value
$cautionBlock = [regex]::Match($toolset, 'CautionTools\s*=\s*new HashSet<string>\(StringComparer\.Ordinal\)\s*\{(?<tools>[\s\S]*?)\n    \};').Groups['tools'].Value
$highImpactBlock = [regex]::Match($toolset, 'HighImpactTools\s*=\s*new HashSet<string>\(StringComparer\.Ordinal\)\s*\{(?<tools>[\s\S]*?)\n    \};').Groups['tools'].Value
$toolsetReadOnly = @([regex]::Matches($readOnlyBlock, '"(zemax_[^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$toolsetCaution = @([regex]::Matches($cautionBlock, '"(zemax_[^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$toolsetHighImpact = @([regex]::Matches($highImpactBlock, '"(zemax_[^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$expectedReadOnly = @($impactByTool.Keys | Where-Object { $impactByTool[$_] -eq 'ReadOnly' } | Sort-Object)
$expectedCaution = @($impactByTool.Keys | Where-Object { $impactByTool[$_] -eq 'Caution' } | Sort-Object)
$expectedHighImpact = @($impactByTool.Keys | Where-Object { $impactByTool[$_] -eq 'HighImpact' } | Sort-Object)
if (@($expectedReadOnly | Where-Object { $_ -notin $toolsetReadOnly }).Count -gt 0 -or @($toolsetReadOnly | Where-Object { $_ -notin $expectedReadOnly }).Count -gt 0) {
    throw "Toolset ReadOnly impact metadata must match the authoritative safety catalogue."
}
if (@($expectedCaution | Where-Object { $_ -notin $toolsetCaution }).Count -gt 0 -or @($toolsetCaution | Where-Object { $_ -notin $expectedCaution }).Count -gt 0) {
    throw "Toolset Caution impact metadata must match the authoritative safety catalogue."
}
if (@($expectedHighImpact | Where-Object { $_ -notin $toolsetHighImpact }).Count -gt 0 -or @($toolsetHighImpact | Where-Object { $_ -notin $expectedHighImpact }).Count -gt 0) {
    throw "Toolset HighImpact metadata must match the authoritative safety catalogue."
}
foreach ($profile in $profiles.Keys) {
    if ($profile -ne 'full-expert') {
        $domainList = ($profileDomains[$profile] | ForEach-Object { '"' + $_ + '"' }) -join ', '
        $expectedCase = $profileCases[$profile] + ' => new[] { ' + $domainList + ' }'
        if ($toolset -notmatch [regex]::Escape($expectedCase)) {
            throw "Toolset '$profile' no longer has its verified explicit domain set."
        }
    }
    elseif ($toolset -notmatch [regex]::Escape('_ => Domains.Select(domain => domain.Id)')) {
        throw "Toolset 'full-expert' must expose every explicit domain."
    }
    foreach ($tool in $profiles[$profile].present) {
        $allowed = $domainByTool[$tool] -in $profileDomains[$profile]
        if ($profile -eq 'basic-viewing') { $allowed = $allowed -and $impactByTool[$tool] -eq 'ReadOnly' }
        if (-not $allowed) { throw "Toolset '$profile' must include '$tool'." }
    }
    foreach ($tool in $profiles[$profile].absent) {
        $allowed = $domainByTool[$tool] -in $profileDomains[$profile]
        if ($profile -eq 'basic-viewing') { $allowed = $allowed -and $impactByTool[$tool] -eq 'ReadOnly' }
        if ($allowed) { throw "Toolset '$profile' must exclude '$tool'." }
    }
}

Write-Host "Explicit operation metadata covers $($sourceCommands.Count) execution commands and $($sourceTools.Count) MCP tools with one verified risk and domain each."
