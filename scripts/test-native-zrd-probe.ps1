param([Parameter(Mandatory=$true)][string]$ZosApiRoot)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Compile this net48 diagnostic using Windows PowerShell 5.1.'}
$framework=[Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
$helper=@((Join-Path $ZosApiRoot 'ZOSAPI_NetHelper.dll'),(Join-Path $ZosApiRoot 'ZOS-API\Libraries\ZOSAPI_NetHelper.dll')) | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
$refs=@((Join-Path $ZosApiRoot 'ZOSAPI_Interfaces.dll'),(Join-Path $ZosApiRoot 'ZOSAPI.dll'),$helper,(Join-Path $framework 'System.dll'),(Join-Path $framework 'System.Core.dll'))
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot 'NativeZrdProbe.cs') -Raw) -ReferencedAssemblies $refs
Write-Host 'Native diagnostic compiled under Windows PowerShell 5.1 against real API references; no licensed application started.'
