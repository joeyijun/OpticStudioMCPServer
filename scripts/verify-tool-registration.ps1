[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$programPath = Join-Path $root "src\ZemaxMCP.Server\Program.cs"
$projectPath = Join-Path $root "src\ZemaxMCP.Server\ZemaxMCP.Server.csproj"
$toolsPath = Join-Path $root "src\ZemaxMCP.Server\Tools"
$registryPath = Join-Path $root "src\ZemaxMCP.Server\Tooling\ZemaxToolAttributes.cs"

if (-not (Test-Path -LiteralPath $programPath)) { throw "Worker bootstrap file not found: $programPath" }
if (-not (Test-Path -LiteralPath $projectPath)) { throw "Worker project file not found: $projectPath" }
if (-not (Test-Path -LiteralPath $toolsPath)) { throw "Tools directory not found: $toolsPath" }
if (-not (Test-Path -LiteralPath $registryPath)) { throw "Worker tool registry not found: $registryPath" }

$program = Get-Content -LiteralPath $programPath -Raw
$project = Get-Content -LiteralPath $projectPath -Raw
$registry = Get-Content -LiteralPath $registryPath -Raw
$toolNames = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new([System.StringComparer]::OrdinalIgnoreCase)
$classesWithoutTools = [System.Collections.Generic.List[string]]::new()
$discovered = 0

Get-ChildItem -LiteralPath $toolsPath -Recurse -Filter "*.cs" -File | ForEach-Object {
    $filePath = $_.FullName
    $source = Get-Content -LiteralPath $_.FullName -Raw
    if ($source -notmatch "\[ZemaxToolType\]") { return }

    $namespaceMatch = [regex]::Match($source, "(?m)^namespace\s+([\w\.]+);")
    $classMatch = [regex]::Match($source, "(?m)^public\s+(?:(?:sealed|abstract)\s+)?class\s+(\w+)")
    if (-not $namespaceMatch.Success -or -not $classMatch.Success) {
        throw "Could not identify the namespace and public Worker tool class in $($_.FullName)"
    }

    $discovered++
    $toolType = "$($namespaceMatch.Groups[1].Value).$($classMatch.Groups[1].Value)"
    $matches = [regex]::Matches($source, '\[ZemaxTool\s*\(\s*Name\s*=\s*"([^"]+)"')
    if ($matches.Count -eq 0) { $classesWithoutTools.Add($toolType) }
    foreach ($match in $matches) {
        $name = $match.Groups[1].Value
        if (-not $toolNames.ContainsKey($name)) {
            $toolNames[$name] = [System.Collections.Generic.List[string]]::new()
        }
        $toolNames[$name].Add($filePath)
    }
}

if ($classesWithoutTools.Count -gt 0) {
    throw "Worker tool classes without a named ZemaxTool method: $($classesWithoutTools -join ', ')"
}

$duplicates = $toolNames.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 }
if ($duplicates) {
    $details = $duplicates | ForEach-Object { "$($_.Key) ($($_.Value -join ', '))" }
    throw "Duplicate Worker tool names found: $($details -join '; ')"
}

if ($toolNames.Count -eq 0) { throw "No named Worker tools were discovered under $toolsPath" }
if ($registry -notmatch 'class\s+WorkerToolRegistry' -or $registry -notmatch 'GetCustomAttribute<ZemaxToolTypeAttribute>') {
    throw "WorkerToolRegistry must discover ZemaxToolType/ZemaxTool metadata at runtime."
}
if ($program -notmatch 'AddSingleton<ZemaxMCP\.Server\.Tooling\.WorkerToolRegistry>') {
    throw "Program.cs must register WorkerToolRegistry with dependency injection."
}
if ($program -match 'AddMcpServer|WithTools<' -or $project -match 'PackageReference Include="ModelContextProtocol"') {
    throw "The protocol-neutral Worker must not register or reference the MCP SDK."
}

$catalogPath = Join-Path $toolsPath "Catalog\ToolCatalogTool.cs"
if (-not (Test-Path -LiteralPath $catalogPath)) { throw "Worker tool catalogue source is missing." }
$catalog = Get-Content -LiteralPath $catalogPath -Raw
if ($catalog -notmatch '\[ZemaxToolType\]' -or $catalog -notmatch 'ZemaxTool\s*\(\s*Name\s*=\s*"zemax_tool_catalog"') {
    throw "The Worker tool catalogue must be discoverable through the native Worker registry."
}

Write-Host "Verified $discovered Worker tool classes and $($toolNames.Count) unique named Worker commands discovered by WorkerToolRegistry."
