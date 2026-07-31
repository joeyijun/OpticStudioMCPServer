param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$updater = Join-Path $root "src\ZemaxMCP.Updater\bin\$Configuration\net48\ZemaxMCP.Updater.exe"
if (-not (Test-Path -LiteralPath $updater)) { throw "Updater build output is missing." }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("ZemaxMCP-updater-test-" + [guid]::NewGuid().ToString("N"))
$install = Join-Path $testRoot "install"
$staging = Join-Path $testRoot "staging"
$failureStaging = Join-Path $testRoot "failure-staging"
try {
  New-Item -ItemType Directory -Force -Path $install, $staging, $failureStaging | Out-Null
  Set-Content -LiteralPath (Join-Path $install "Start-Zemax-MCP.exe") -Value "old-launcher"
  Set-Content -LiteralPath (Join-Path $install "obsolete.txt") -Value "old-state"
  New-Item -ItemType Directory -Force -Path (Join-Path $install "logs") | Out-Null
  Set-Content -LiteralPath (Join-Path $install "logs\preserved.log") -Value "runtime-state"
  Set-Content -LiteralPath (Join-Path $staging "Start-Zemax-MCP.exe") -Value "new-launcher"
  Set-Content -LiteralPath (Join-Path $staging "added.txt") -Value "new-state"
  & $updater --staging $staging --install $install --parent-pid 0 --restart false
  if ($LASTEXITCODE -ne 0 -or (Get-Content -Raw (Join-Path $install "Start-Zemax-MCP.exe")).Trim() -ne "new-launcher" -or
      -not (Test-Path -LiteralPath (Join-Path $install "added.txt")) -or (Test-Path -LiteralPath (Join-Path $install "obsolete.txt")) -or
      (Get-Content -Raw (Join-Path $install "logs\preserved.log")).Trim() -ne "runtime-state") {
    throw "Updater did not apply a valid staged update correctly."
  }

  Set-Content -LiteralPath (Join-Path $failureStaging "Start-Zemax-MCP.exe") -Value "broken-launcher"
  $lockedFile = Join-Path $failureStaging "locked.bin"
  Set-Content -LiteralPath $lockedFile -Value "cannot-copy"
  $lock = [IO.File]::Open($lockedFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
  try { & $updater --staging $failureStaging --install $install --parent-pid 0 --restart false }
  finally { $lock.Dispose() }
  if ($LASTEXITCODE -eq 0) { throw "The deliberately unreadable update unexpectedly succeeded." }
  if ((Get-Content -Raw (Join-Path $install "Start-Zemax-MCP.exe")).Trim() -ne "new-launcher" -or
      (Get-Content -Raw (Join-Path $install "logs\preserved.log")).Trim() -ne "runtime-state") {
    throw "Updater rollback did not restore the previous installation."
  }
}
finally {
  if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
Write-Host "Updater apply and rollback behavior verified."
# The rollback scenario intentionally executes the updater once with exit code
# 1. All assertions above have verified that failure and its recovery, so do
# not leak the expected native exit code into the hosting PowerShell process.
$global:LASTEXITCODE = 0
