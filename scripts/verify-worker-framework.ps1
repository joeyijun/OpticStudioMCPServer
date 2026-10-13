param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$unsupported = '\b(?:double|float)\.IsFinite\b|\bMath\.Clamp\b|\bPath\.IsPathFullyQualified\b|\bConvert\.ToHexString\b|\bSHA256\.HashData\b'
foreach ($folder in @('ZemaxMCP.Server','ZemaxMCP.Core','ZemaxMCP.Launcher')) {
    $files = Get-ChildItem -LiteralPath (Join-Path $root ('src\'+$folder)) -Recurse -File -Filter *.cs |
        Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'}
    foreach ($file in $files) {
        if ([IO.File]::ReadAllText($file.FullName) -match $unsupported) {
            throw ('Unsupported modern BCL API in net48 source: '+$file.FullName)
        }
    }
}
$project = Join-Path $root 'tests\ZemaxMCP.FrameworkCompatibilityTests\ZemaxMCP.FrameworkCompatibilityTests.csproj'
[xml]$modern = Get-Content -Raw (Join-Path $root 'tests\ZemaxMCP.SimulationTests\ZemaxMCP.SimulationTests.csproj')
[xml]$framework = Get-Content -Raw $project
$frameworkSources = @($framework.Project.ItemGroup.Compile | ForEach-Object {$_.Include})
foreach ($source in @($modern.Project.ItemGroup.Compile | ForEach-Object {$_.Include})) {
    if ($source -like '*\ZemaxMCP.Server\Tools\*' -and $source -notin $frameworkSources) {
        throw ('A simulated optical kernel lacks net48 compile coverage: '+$source)
    }
}
dotnet build $project -c $Configuration
if ($LASTEXITCODE) { throw 'net48 optical kernel projection did not compile.' }
& (Join-Path $root "tests\ZemaxMCP.FrameworkCompatibilityTests\bin\$Configuration\net48\FrameworkChecks.exe")
if ($LASTEXITCODE) { throw 'net48 compatibility assertions failed.' }
Write-Output 'Vendor-independent net48 projection passed. Complete Worker compilation still requires a licensed local ZOS-API reference.'
