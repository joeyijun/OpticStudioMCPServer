param(
  [string]$Configuration = "Release",
  [Parameter(Mandatory = $true)][string]$ManifestPath,
  [Parameter(Mandatory = $true)][string]$PackagePath,
  [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$launcher = Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"
$assembly = [Reflection.Assembly]::LoadFrom($launcher)
$type = $assembly.GetType("ZemaxMCP.Launcher.UpdateManifestVerifier", $true)
$method = $type.GetMethod("Verify", [Reflection.BindingFlags]"Public,Static")
[string]$manifest = Get-Content -Raw -LiteralPath $ManifestPath
[string]$package = (Resolve-Path -LiteralPath $PackagePath).Path
$assetName = [IO.Path]::GetFileName($package)
$arguments = [object[]]@($manifest, [string]$Version, [string]$assetName, $package)
$result = $method.Invoke($null, $arguments)
if ($result.Version -ne $Version) { throw "Signed manifest verification returned the wrong version." }

try {
  $badArguments = [object[]]@($manifest, [string]($Version + "-tampered"), [string]$assetName, $package)
  $method.Invoke($null, $badArguments) | Out-Null
  throw "A mismatched release context was accepted."
}
catch {
  $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
  if ($message -notmatch "version") { throw }
}

$manifestObject = $manifest | ConvertFrom-Json
$signatureBytes = [Convert]::FromBase64String($manifestObject.signature)
$signatureBytes[0] = $signatureBytes[0] -bxor 1
$manifestObject.signature = [Convert]::ToBase64String($signatureBytes)
[string]$tamperedManifest = $manifestObject | ConvertTo-Json -Compress
try {
  $badArguments = [object[]]@([string]$tamperedManifest, [string]$Version, [string]$assetName, [string]$package)
  $method.Invoke($null, $badArguments) | Out-Null
  throw "A manifest with a tampered signature was accepted."
}
catch {
  $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
  if ($message -notmatch "signature") { throw }
}

$tamperedPackage = Join-Path ([IO.Path]::GetTempPath()) ("ZemaxMCP-tampered-" + [guid]::NewGuid().ToString("N") + ".zip")
try {
  [byte[]]$packageBytes = [IO.File]::ReadAllBytes($package)
  if ($packageBytes.Length -eq 0) { throw "The test package is empty." }
  $packageBytes[0] = $packageBytes[0] -bxor 1
  [IO.File]::WriteAllBytes($tamperedPackage, $packageBytes)
  $badArguments = [object[]]@([string]$manifest, [string]$Version, [string]$assetName, [string]$tamperedPackage)
  try {
    $method.Invoke($null, $badArguments) | Out-Null
    throw "A package with a mismatched SHA256 was accepted."
  }
  catch {
    $message = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
    if ($message -notmatch "SHA256") { throw }
  }
}
finally {
  Remove-Item -LiteralPath $tamperedPackage -Force -ErrorAction SilentlyContinue
}
Write-Host "Signed update manifest accepted; version, signature, and package tampering rejected."
