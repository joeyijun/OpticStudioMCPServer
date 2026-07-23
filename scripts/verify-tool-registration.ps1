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
$discovered = 0

Get-ChildItem -LiteralPath $toolsPath -Recurse -Filter "*Tool.cs" -File | ForEach-Object {
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
}

if ($missing.Count -gt 0) {
    throw "MCP tool classes missing explicit Program.cs registration: $($missing -join ', ')"
}

Write-Host "Verified $discovered MCP tool classes are registered in Program.cs."
