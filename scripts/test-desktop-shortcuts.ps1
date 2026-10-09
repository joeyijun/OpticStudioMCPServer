param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"))
$migrate = $assembly.GetType('ZemaxMCP.DesktopShared.DesktopShortcut').GetMethod('MigrateLegacy', [Reflection.BindingFlags]'Static,NonPublic')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-shortcuts-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$shell = New-Object -ComObject WScript.Shell
try {
    $target = Join-Path $fixture 'Start-Zemax-MCP.exe'
    $legacy = Join-Path $fixture 'Start Zemax MCP.lnk'
    $current = Join-Path $fixture 'Zemax MCP.lnk'
    $invokeArguments = New-Object object[] 2
    $invokeArguments[0] = [string]$fixture
    $invokeArguments[1] = [string]$target
    $link = $shell.CreateShortcut($legacy); $link.TargetPath=$target; $link.Arguments='--preserved'; $link.Save()
    [void]$migrate.Invoke($null,$invokeArguments)
    if ((Test-Path -LiteralPath $legacy) -or !(Test-Path -LiteralPath $current) -or $shell.CreateShortcut($current).Arguments -ne '--preserved') { throw 'Managed shortcut migration/preserved arguments failed.' }
    $link = $shell.CreateShortcut($legacy); $link.TargetPath=(Join-Path $fixture 'Other.exe'); $link.Save()
    [void]$migrate.Invoke($null,$invokeArguments)
    if (!(Test-Path -LiteralPath $legacy)) { throw 'Foreign shortcut was modified.' }
    $link.TargetPath=$target; $link.Save()
    [void]$migrate.Invoke($null,$invokeArguments)
    if (Test-Path -LiteralPath $legacy) { throw 'Matching legacy duplicate was not removed.' }
    $link = $shell.CreateShortcut($legacy); $link.TargetPath=$target; $link.Save()
    $link = $shell.CreateShortcut($current); $link.TargetPath=(Join-Path $fixture 'Other.exe'); $link.Save()
    [void]$migrate.Invoke($null,$invokeArguments)
    if (!(Test-Path -LiteralPath $legacy) -or $shell.CreateShortcut($current).TargetPath -ne (Join-Path $fixture 'Other.exe')) { throw 'Custom destination shortcut was overwritten.' }
    Write-Output 'Desktop shortcut migration: renamed, preserved arguments, idempotent, foreign targets and custom destination protected.'
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    # Only exact files created inside this unique fixture are removed.
    foreach ($path in @($legacy,$current)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    [IO.Directory]::Delete($fixture)
}
