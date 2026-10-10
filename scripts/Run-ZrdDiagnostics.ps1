[CmdletBinding()]
param([string]$ZemaxRoot='')
$ErrorActionPreference='Stop'
if([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use Windows PowerShell -STA.'}
Write-Host 'Independent ZRD diagnostic: your normal MCP service and official Samples are not modified.'
Write-Host 'Requires one spare Standalone license instance. A new temporary model and ZRD are retained.'
if([string]::IsNullOrWhiteSpace($ZemaxRoot)) {
    $candidates=@(Get-ChildItem -LiteralPath $env:ProgramFiles -Directory | Where-Object Name -match 'Zemax|OpticStudio' | Where-Object {Test-Path (Join-Path $_.FullName 'ZOSAPI_Interfaces.dll')})
    $ZemaxRoot=($candidates | Sort-Object Name -Descending | Select-Object -First 1).FullName
}
if([string]::IsNullOrWhiteSpace($ZemaxRoot)){throw 'No local OpticStudio found; supply -ZemaxRoot with its installation folder.'}
$ZemaxRoot=(Resolve-Path -LiteralPath $ZemaxRoot).Path
$helper=@((Join-Path $ZemaxRoot 'ZOSAPI_NetHelper.dll'),(Join-Path $ZemaxRoot 'ZOS-API\Libraries\ZOSAPI_NetHelper.dll')) | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
if(-not $helper){throw 'Local ZOSAPI_NetHelper.dll not found.'}
$assemblies=@((Join-Path $ZemaxRoot 'ZOSAPI_Interfaces.dll'),(Join-Path $ZemaxRoot 'ZOSAPI.dll'),$helper)
foreach($path in $assemblies){[Reflection.Assembly]::LoadFrom($path) | Out-Null}
$framework=[Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'NativeZrdProbe.cs') -Raw) -ReferencedAssemblies ($assemblies+@((Join-Path $framework 'System.dll'),(Join-Path $framework 'System.Core.dll')))
$samples=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Zemax\Samples'
$sample=Join-Path $samples 'Non-sequential\Ray splitting\Beam splitter.ZMX'
$archive=Join-Path $samples 'Short course\Archive\sc_stray2.ZRD'
if(-not (Test-Path -LiteralPath $sample)){throw "Official fixture not found: $sample"}
$output=Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-zrd-probe-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$report=Join-Path $output 'zrd-diagnostic.json'
try {
    $reads=[NativeZrdProbe]::Run($ZemaxRoot,$sample,$archive,$output)
    @{testedAt=[DateTimeOffset]::Now.ToString('o');outputDirectory=$output;reads=@($reads)} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding UTF8
    $reads | Format-Table Label,Rays,Segments,UnexpectedParents,AppliedFilter,Error -AutoSize
    if(@($reads | Where-Object { $_.Label -eq 'fresh/empty-filter' -and $_.Rays -gt 0 -and -not $_.Error }).Count -ne 1){throw 'Fresh native ZRD positive-path check failed. Preserve the report.'}
    Write-Host "Fresh native ZRD read passed. Return this report: $report"
} catch {
    if(-not (Test-Path -LiteralPath $report)){@{error=$_.Exception.Message;outputDirectory=$output} | ConvertTo-Json | Set-Content -LiteralPath $report -Encoding UTF8}
    Write-Host "Diagnostic report retained at: $report"
    throw
}
