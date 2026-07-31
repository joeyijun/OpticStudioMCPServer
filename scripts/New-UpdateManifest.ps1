param(
  [Parameter(Mandatory = $true)][string]$PackagePath,
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$PrivateKeyBase64 = $env:UPDATE_SIGNING_PRIVATE_KEY_B64,
  [string]$OutputPath = (Join-Path (Split-Path $PackagePath -Parent) "release-manifest.json")
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Update package not found: $PackagePath" }
if ([string]::IsNullOrWhiteSpace($PrivateKeyBase64)) { throw "UPDATE_SIGNING_PRIVATE_KEY_B64 is required to sign an update manifest." }

$package = Get-Item -LiteralPath $PackagePath
$payloadObject = [ordered]@{
  schemaVersion = 1
  version = $Version
  assetName = $package.Name
  sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $package.FullName).Hash.ToLowerInvariant()
  size = $package.Length
}
$payloadJson = $payloadObject | ConvertTo-Json -Compress
$payloadBytes = [Text.Encoding]::UTF8.GetBytes($payloadJson)
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new()
try {
  $rsa.ImportCspBlob([Convert]::FromBase64String($PrivateKeyBase64.Trim()))
  $signature = $rsa.SignData($payloadBytes, [Security.Cryptography.SHA256]::Create())
}
finally { $rsa.Dispose() }

$manifest = [ordered]@{
  algorithm = "RS256"
  payload = [Convert]::ToBase64String($payloadBytes)
  signature = [Convert]::ToBase64String($signature)
}
[IO.File]::WriteAllText($OutputPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Host "Signed update manifest: $OutputPath"
