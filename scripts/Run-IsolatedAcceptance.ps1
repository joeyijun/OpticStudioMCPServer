[CmdletBinding()]
param(
    [string]$PackageRoot = '',
    [string]$ZemaxRoot = '',
    [string]$SamplesRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Zemax\Samples'),
    [switch]$AllowWorkerTermination,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    # Windows PowerShell 5.1 -File does not reliably expose PSScriptRoot during parameter binding.
    $PackageRoot = Join-Path $PSScriptRoot 'runtime'
}
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
foreach ($file in @('Host\ZemaxMCP.Host.exe', 'ZemaxMCP.Worker.exe', 'VERSION.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $file) -PathType Leaf)) { throw "Missing package file: $file" }
}
if (-not $ZemaxRoot) {
    $settingsPath = Join-Path $env:LOCALAPPDATA 'ZemaxMCP\launcher-settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $preferences = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $ZemaxRoot = [string]$preferences.zemaxRoot
    }
}
if (-not $ZemaxRoot -or -not (Test-Path -LiteralPath (Join-Path $ZemaxRoot 'ZOSAPI.dll'))) {
    throw 'Run on the OpticStudio computer; pass -ZemaxRoot if its installation was not found in Launcher preferences.'
}
$ZemaxRoot = (Resolve-Path -LiteralPath $ZemaxRoot).Path
$phases = @(
    @{Name='sequential'; Fixture='Sequential\Objectives\Cooke 40 degree field.zmx'; Flags=@{}},
    @{Name='nsc'; Fixture='Non-sequential\Sources\Diode sample.ZMX'; Flags=@{VerifyNsc=$true; VerifyOfficialTasks=$true}},
    @{Name='tolerance'; Fixture='Short course\Optical System Design Using OpticStudio\SC_Tol_Cooke.zmx'; Flags=@{VerifyTolerance=$true; VerifyOfficialTasks=$true}},
    @{Name='optimization-recovery'; Fixture='Short course\Optical System Design Using OpticStudio\Advanced_SC_doubleGauss_final.ZMX';
      Flags=@{VerifyOptimization=$true; VerifyBackgroundJobs=$true; VerifyOfficialTasks=$true; VerifyTaskCancellation=$true; VerifyWorkerCrashRecovery=$true}}
)
foreach ($phase in $phases) {
    $phase.Path = Join-Path $SamplesRoot $phase.Fixture
    if (-not (Test-Path -LiteralPath $phase.Path -PathType Leaf)) { throw "Sample missing: $($phase.Path)" }
}
if ($PlanOnly) { foreach ($phase in $phases) { [pscustomobject]@{Name=$phase.Name;Path=$phase.Path} }; return }
if (-not $AllowWorkerTermination) {
    throw 'This isolated test terminates only its own checked Worker. Rerun with -AllowWorkerTermination after saving normal work.'
}
# Fail before starting services when corporate policy blocks the required local PID/path checks.
$selfProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction Stop
if (-not $selfProcess.ExecutablePath) { throw 'Local process ownership checks are unavailable; no test service was started.' }
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-isolated-' + [Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $runRoot 'runtime'
$resultsRoot = Join-Path $PSScriptRoot ('results-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory($runtime)
[void][IO.Directory]::CreateDirectory($resultsRoot)
Copy-Item (Join-Path $PackageRoot '*') -Destination $runtime -Recurse -Exclude 'launcher-settings.json','launcher-settings.json.bak','clients.json','logs','snapshots','shortcut-icons','update.log','.update.lock'
$hostExe = Join-Path $runtime 'Host\ZemaxMCP.Host.exe'
$workerExe = Join-Path $runtime 'ZemaxMCP.Worker.exe'
$summary = [Collections.Generic.List[object]]::new()
$runCompleted = $false
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 32
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$testToken = [Convert]::ToBase64String($bytes)
function Stop-OwnedTestService($process) {
    if ($null -eq $process) { return }
    # Never use a LAN-returned PID or a name-wide process kill.
    try { $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" -ErrorAction Stop) }
    finally { if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(10000) } }
    foreach ($child in $children) {
        if ($child.Name -eq 'ZemaxMCP.Worker.exe' -and $child.ExecutablePath -and
            [string]::Equals([IO.Path]::GetFullPath([string]$child.ExecutablePath), $workerExe, [StringComparison]::OrdinalIgnoreCase)) {
            $workerProcess = Get-Process -Id ([int]$child.ProcessId) -ErrorAction SilentlyContinue
            if ($workerProcess -and -not $workerProcess.WaitForExit(15000)) {
                $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($child.ProcessId)"
                if ($current -and $current.ParentProcessId -eq $process.Id -and $current.ExecutablePath -eq $workerExe) {
                    Stop-Process -Id ([int]$child.ProcessId) -Force -ErrorAction Stop
                }
            }
            if ($workerProcess) { $workerProcess.Dispose() }
        }
    }
    $process.Dispose()
}
try {
    foreach ($phase in $phases) {
        $process = $null
        $phaseDirectory = Join-Path $resultsRoot $phase.Name
        [void][IO.Directory]::CreateDirectory($phaseDirectory)
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $endpoint = "http://127.0.0.1:$port/mcp"
        $arguments = '--host 127.0.0.1 --port ' + $port + ' --worker "' + $workerExe + '" --zemax-root "' + $ZemaxRoot +
            '" --enable-official-tasks true --log-dir "' + $phaseDirectory + '\logs" --snapshot-dir "' + $phaseDirectory + '\snapshots"'
        $priorToken = $env:ZEMAX_MCP_TOKEN
        $priorClients = $env:ZEMAX_MCP_CLIENTS_FILE
        try {
            $env:ZEMAX_MCP_TOKEN = $testToken
            $env:ZEMAX_MCP_CLIENTS_FILE = $null
            $process = Start-Process -FilePath $hostExe -ArgumentList $arguments -WorkingDirectory $runtime -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $phaseDirectory 'host.stdout.log') -RedirectStandardError (Join-Path $phaseDirectory 'host.stderr.log')
        } finally { $env:ZEMAX_MCP_TOKEN=$priorToken; $env:ZEMAX_MCP_CLIENTS_FILE=$priorClients }
        try {
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
            $ready = $false
            do {
                if ($process.HasExited) { throw "Independent Host exited ($($process.ExitCode))." }
                try {
                    $health = Invoke-RestMethod ($endpoint+'/health') -Headers @{Authorization=('Bearer '+$testToken)} -TimeoutSec 10
                    $ready = $health.zosApiConnected -and $health.licenseValidForApi -eq $true
                } catch { $ready=$false }
                if (-not $ready) { Start-Sleep -Milliseconds 300 }
            } while (-not $ready -and [DateTimeOffset]::UtcNow -lt $deadline)
            if (-not $ready) { throw 'Independent Worker did not obtain a valid API license/connection. Normal service was not stopped.' }
            $checks = @{FixturePath=$phase.Path; Endpoint=$endpoint; AccessToken=$testToken; AllowReplaceCurrentSystem=$true;
                ReportPath=(Join-Path $phaseDirectory 'functional.json'); JobWaitSeconds=180}
            foreach ($key in $phase.Flags.Keys) { $checks[$key]=$phase.Flags[$key] }
            if ($phase.Flags.VerifyWorkerCrashRecovery) {
                $checks.AllowWorkerTermination=$true
                $checks.ExpectedWorkerPath=$workerExe
                $checks.ExpectedHostProcessId=$process.Id
            }
            & (Join-Path $PSScriptRoot 'verify-live-functional.ps1') @checks
            $summary.Add([pscustomobject]@{Phase=$phase.Name;Passed=$true;Report=$checks.ReportPath})
        } catch {
            $summary.Add([pscustomobject]@{Phase=$phase.Name;Passed=$false;Error=$_.Exception.Message})
            throw
        } finally { Stop-OwnedTestService $process }
    }
    $runCompleted = $true
} finally {
    [ordered]@{GeneratedAt=[DateTimeOffset]::Now;PackageVersion=(Get-Content (Join-Path $PackageRoot 'VERSION.txt') -Raw).Trim();
        Passed=($runCompleted -and $summary.Count -eq $phases.Count -and @($summary | Where-Object Passed -ne $true).Count -eq 0);
        RuntimeRetained=$runRoot;Tests=$summary;HardRecoveryEvidence='Worker crash/restart only; not a non-cooperative COM grace-timeout injection.'} |
        ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $resultsRoot 'summary.json') -Encoding UTF8
    Write-Host "Acceptance results: $resultsRoot"
    Write-Host 'Normal installation, settings, service port and official Samples were not modified.'
    Write-Host "Test runtime retained for diagnostics: $runRoot"
}
