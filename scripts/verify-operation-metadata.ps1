$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$toolsRoot = Join-Path $root "src\ZemaxMCP.Server\Tools"
$metadataPath = Join-Path $root "src\ZemaxMCP.Core\Session\ZemaxOperationMetadata.cs"
$catalogPath = Join-Path $toolsRoot "Catalog\ToolCatalogTool.cs"

$sourceFiles = @(Get-ChildItem -LiteralPath $toolsRoot -Recurse -Filter "*.cs" -File)
$sourceCommands = @($sourceFiles | Select-String -Pattern 'ExecuteAsync\("([^"]+)"' -AllMatches | ForEach-Object {
    foreach ($match in $_.Matches) { $match.Groups[1].Value }
} | Sort-Object -Unique)
$sourceTools = @($sourceFiles | Select-String -Pattern 'McpServerTool\(Name = "([^"]+)"\)' -AllMatches | ForEach-Object {
    foreach ($match in $_.Matches) { $match.Groups[1].Value }
} | Sort-Object -Unique)
$metadata = Get-Content -Raw $metadataPath
$metadataCommands = @([regex]::Matches($metadata, '"[A-Za-z][A-Za-z0-9]+"') | ForEach-Object {
    $_.Value.Trim('"')
} | Where-Object { $_ -notlike 'zemax_*' } | Sort-Object -Unique)
$metadataTools = @([regex]::Matches($metadata, '"zemax_[^"]+"') | ForEach-Object {
    $_.Value.Trim('"')
} | Sort-Object -Unique)

$missingCommands = @($sourceCommands | Where-Object { $_ -notin $metadataCommands })
$missingTools = @($sourceTools | Where-Object { $_ -notin $metadataTools })
if ($missingCommands.Count -gt 0) { throw "Commands missing explicit safety metadata: $($missingCommands -join ', ')" }
if ($missingTools.Count -gt 0) { throw "MCP tools missing explicit safety metadata: $($missingTools -join ', ')" }
if ((Get-Content -Raw $catalogPath) -match 'StartsWith\("zemax_(set|add|delete|remove|clear|calculate)_') {
    throw "Tool catalog must use ZemaxOperationMetadata instead of name-prefix risk rules."
}
if ($metadata -match 'MutatingPrefixes') { throw "Safety metadata must not use command-name prefixes." }
Write-Host "Explicit operation metadata covers $($sourceCommands.Count) execution commands and $($sourceTools.Count) MCP tools."
