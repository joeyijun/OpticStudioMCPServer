param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "src/ZemaxMCP.Launcher/bin/$Configuration/net48/Start-Zemax-MCP.exe"))
$type = $assembly.GetType('ZemaxMCP.Launcher.UpdatePackageService', $true)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$parse = $type.GetMethod('ParseReleaseVersion', $flags)
foreach ($tag in @('v1.5.1','1.5.1-rc.1')) {
    if ($parse.Invoke($null, [object[]]@($tag)).ToString(3) -ne '1.5.1') { throw 'Release version parsing regressed.' }
}
$rejected = $false
try { $parse.Invoke($null, [object[]]@('invalid-version')) | Out-Null }
catch { $rejected = $true }
if (!$rejected) { throw 'Invalid release version accepted.' }

$directory = Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-package-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
try {
    $extract = $type.GetMethod('VerifyAndExtractAsync', $flags)
    $zip = Join-Path $directory 'missing.zip'
    $rejected = $false
    try {
        $arguments = New-Object object[] 5
        $arguments[0] = [string]'{}'
        $arguments[1] = [string]'v1.5.1'
        $arguments[2] = [string]$zip
        $arguments[3] = [string]$directory
        $arguments[4] = [Threading.CancellationToken]::None
        $task = $extract.Invoke($null, $arguments)
        $task.GetAwaiter().GetResult()
    } catch {
        if ($null -eq $task -or !$task.IsFaulted) { throw }
        $rejected = $true
    }
    if (!$rejected -or @(Get-ChildItem -LiteralPath $directory).Count -ne 0) { throw 'Unsigned package extraction was accepted.' }
    $cancellation = New-Object Threading.CancellationTokenSource
    try {
        $cancellation.Cancel()
        $arguments[4] = [Threading.CancellationToken]$cancellation.Token
        $task = $extract.Invoke($null, $arguments)
        $rejected = $false
        try { $task.GetAwaiter().GetResult() } catch { $rejected = $true }
        if (!$rejected -or !$task.IsCanceled) { throw 'Cancelled package preparation continued.' }
    } finally { $cancellation.Dispose() }
    Write-Output 'UI-independent update package service: version parsing, unsigned rejection and cancellation passed.'
} finally {
    # Only this newly generated test directory may be removed.
    if ([IO.Path]::GetFullPath($directory).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}
