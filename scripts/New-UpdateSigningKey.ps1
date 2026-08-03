param(
  [Parameter(Mandatory = $true)][string]$PrivateKeyOutput
)

$ErrorActionPreference = "Stop"
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
try {
  $privateKey = [Convert]::ToBase64String($rsa.ExportCspBlob($true))
  $publicKey = [Convert]::ToBase64String($rsa.ExportCspBlob($false))
  $directory = Split-Path $PrivateKeyOutput -Parent
  if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
  [IO.File]::WriteAllText($PrivateKeyOutput, $privateKey, [Text.Encoding]::ASCII)
  Write-Output $publicKey
}
finally { $rsa.Dispose() }
