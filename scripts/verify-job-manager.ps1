param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$serverDir = Join-Path $root "src\ZemaxMCP.Server\bin\$Configuration\net48"
$server = Join-Path $serverDir "ZemaxMCP.Worker.exe"
$fixture = Join-Path $PSScriptRoot "fixtures\VerifyJobManager.cs"
$verifier = Join-Path $serverDir "VerifyJobManager.exe"
if (-not (Test-Path -LiteralPath $server)) { throw "Build the MCP server before verifying the job manager." }
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) { throw "The .NET Framework C# compiler is required for job-manager verification." }
try {
  & $csc /nologo /target:exe /out:$verifier /r:$server $fixture
  if ($LASTEXITCODE -ne 0) { throw "Could not compile the job-manager verifier." }
  & $verifier
  if ($LASTEXITCODE -ne 0) { throw "The job-manager verifier failed." }
  Write-Host "Long-job queue, progress/result retention, and queued cancellation verified."
}
finally {
  Remove-Item -LiteralPath $verifier -Force -ErrorAction SilentlyContinue
}
