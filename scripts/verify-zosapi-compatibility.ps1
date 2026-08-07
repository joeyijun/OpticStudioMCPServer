[CmdletBinding()]
param(
    [string[]]$ZemaxRoots = @(),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$workerProject = Join-Path $repoRoot "src\ZemaxMCP.Server\ZemaxMCP.Server.csproj"

if ($ZemaxRoots.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:ZEMAX_COMPAT_ROOTS)) {
    $ZemaxRoots = @($env:ZEMAX_COMPAT_ROOTS -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
if ($ZemaxRoots.Count -eq 0) {
    throw "Provide -ZemaxRoots with one or more installed OpticStudio folders, or set ZEMAX_COMPAT_ROOTS to a semicolon-separated list. This check only compiles against the installed ZOS-API DLLs; it does not start OpticStudio or consume a license."
}

function Find-NetHelper([string]$Root) {
    foreach ($candidate in @(
        (Join-Path $Root "ZOSAPI_NetHelper.dll"),
        (Join-Path $Root "ZOS-API\Libraries\ZOSAPI_NetHelper.dll"),
        (Join-Path $Root "ZOS_API\Libraries\ZOSAPI_NetHelper.dll")
    )) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

function Read-Version([string]$Path) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if (-not [string]::IsNullOrWhiteSpace($info.FileVersion)) { return $info.FileVersion }
    if (-not [string]::IsNullOrWhiteSpace($info.ProductVersion)) { return $info.ProductVersion }
    return [Reflection.AssemblyName]::GetAssemblyName($Path).Version.ToString()
}

$results = New-Object System.Collections.Generic.List[object]
$failed = New-Object System.Collections.Generic.List[string]
foreach ($requestedRoot in $ZemaxRoots) {
    $resolved = (Resolve-Path -LiteralPath $requestedRoot.Trim().Trim('"')).Path
    $zosApi = Join-Path $resolved "ZOSAPI.dll"
    $interfaces = Join-Path $resolved "ZOSAPI_Interfaces.dll"
    $netHelper = Find-NetHelper $resolved
    if (-not (Test-Path -LiteralPath $zosApi) -or -not (Test-Path -LiteralPath $interfaces) -or [string]::IsNullOrWhiteSpace($netHelper)) {
        $failed.Add("$resolved: required ZOSAPI.dll, ZOSAPI_Interfaces.dll, or ZOSAPI_NetHelper.dll is missing")
        continue
    }

    $version = Read-Version $interfaces
    Write-Host "Compiling Worker against ZOS-API $version from $resolved" -ForegroundColor Cyan
    & dotnet build $workerProject -c $Configuration -t:Rebuild "-p:ZEMAX_ROOT=$resolved" "-p:ZOSAPI_NETHELPER_PATH=$netHelper"
    $exitCode = $LASTEXITCODE
    $results.Add([PSCustomObject]@{
        Root = $resolved
        InterfacesVersion = $version
        ZosApiVersion = Read-Version $zosApi
        NetHelperVersion = Read-Version $netHelper
        Compile = if ($exitCode -eq 0) { "PASS" } else { "FAIL" }
    })
    if ($exitCode -ne 0) {
        $failed.Add("$resolved (ZOSAPI_Interfaces $version): Worker compile failed")
    }
}

$results | Format-Table -AutoSize
if ($failed.Count -gt 0) {
    throw "ZOS-API compatibility compile failures:`n - $($failed -join "`n - ")"
}

Write-Host "All requested OpticStudio ZOS-API baselines compiled successfully. This proves source/API-surface compatibility only; licensed runtime acceptance is still required for behavior." -ForegroundColor Green
