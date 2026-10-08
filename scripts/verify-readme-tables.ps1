[CmdletBinding()]
param(
    [string]$ReadmePath = (Join-Path (Split-Path $PSScriptRoot -Parent) "README.md")
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $ReadmePath -PathType Leaf)) {
    throw "README missing: $ReadmePath"
}

# Count non-escaped Markdown pipes. README table cells may contain Markdown
# links/backticks but table layout requires the same number of columns.
function Get-ColumnCount {
    param([string]$Row)
    $pipes = [regex]::Matches($Row, '(?<!\\)\|').Count
    if ($Row.TrimStart().StartsWith("|")) { $pipes-- }
    if ($Row.TrimEnd().EndsWith("|")) { $pipes-- }
    return $pipes + 1
}

$lines = @(Get-Content -LiteralPath $ReadmePath)
$tables = 0
for ($i = 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch '^\s*\|(?:\s*:?-{3,}:?\s*\|){2,}\s*$') { continue }
    $expected = Get-ColumnCount $lines[$i]
    if (-not $lines[$i-1].TrimStart().StartsWith("|") -or
        (Get-ColumnCount $lines[$i-1]) -ne $expected) {
        throw "README table header has inconsistent column count at line $($i+1). Expected $expected columns."
    }
    $tables++
    $row = $i + 1
    while ($row -lt $lines.Count -and $lines[$row].TrimStart().StartsWith("|")) {
        $actual = Get-ColumnCount $lines[$row]
        if ($actual -ne $expected) {
            throw "README table row $($row+1) has $actual columns, expected $expected."
        }
        $row++
    }
}
if ($tables -eq 0) { throw "No Markdown tables were checked in README." }
Write-Host "README Markdown table structure passed ($tables tables)."
