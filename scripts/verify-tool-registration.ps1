[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$programPath = Join-Path $root "src\ZemaxMCP.Server\Program.cs"
$toolsPath = Join-Path $root "src\ZemaxMCP.Server\Tools"

if (-not (Test-Path -LiteralPath $programPath)) { throw "Server registration file not found: $programPath" }
if (-not (Test-Path -LiteralPath $toolsPath)) { throw "Tools directory not found: $toolsPath" }

$program = Get-Content -LiteralPath $programPath -Raw
$missing = [System.Collections.Generic.List[string]]::new()
$toolNames = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new([System.StringComparer]::OrdinalIgnoreCase)
$discovered = 0

Get-ChildItem -LiteralPath $toolsPath -Recurse -Filter "*.cs" -File | ForEach-Object {
    $filePath = $_.FullName
    $source = Get-Content -LiteralPath $_.FullName -Raw
    if ($source -notmatch "\[McpServerToolType\]") { return }

    $namespaceMatch = [regex]::Match($source, "(?m)^namespace\s+([\w\.]+);")
    $classMatch = [regex]::Match($source, "(?m)^public\s+(?:(?:sealed|abstract)\s+)?class\s+(\w+)")
    if (-not $namespaceMatch.Success -or -not $classMatch.Success) {
        throw "Could not identify the namespace and public tool class in $($_.FullName)"
    }

    $discovered++
    $toolType = "$($namespaceMatch.Groups[1].Value).$($classMatch.Groups[1].Value)"
    if (-not $program.Contains("WithTools<$toolType>()")) { $missing.Add($toolType) }

    [regex]::Matches($source, '\[McpServerTool\s*\(\s*Name\s*=\s*"([^"]+)"') | ForEach-Object {
        $name = $_.Groups[1].Value
        if (-not $toolNames.ContainsKey($name)) {
            $toolNames[$name] = [System.Collections.Generic.List[string]]::new()
        }
        $toolNames[$name].Add($filePath)
    }
}

if ($missing.Count -gt 0) {
    throw "MCP tool classes missing explicit Program.cs registration: $($missing -join ', ')"
}

$duplicates = $toolNames.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 }
if ($duplicates) {
    $details = $duplicates | ForEach-Object { "$($_.Key) ($($_.Value -join ', '))" }
    throw "Duplicate MCP tool names found: $($details -join '; ')"
}

if ($toolNames.Count -eq 0) { throw "No named MCP tools were discovered under $toolsPath" }

$catalogPath = Join-Path $toolsPath "Catalog\ToolCatalogTool.cs"
if (-not (Test-Path -LiteralPath $catalogPath) -or
    -not $program.Contains("WithTools<ZemaxMCP.Server.Tools.Catalog.ToolCatalogTool>()")) {
    throw "The generated MCP tool catalogue must be registered with the server."
}

Write-Host "Verified $discovered MCP tool classes and $($toolNames.Count) unique named MCP methods are registered in Program.cs."
