[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Endpoint,
    [string]$AccessToken=$env:ZEMAX_MCP_TOKEN,
    [string]$ClientInstanceId='',
    [Parameter(Mandatory=$true)][string]$SequentialFixture,
    [string]$NscFixture='',
    [string]$NativeZrdFixture='',
    [Parameter(Mandatory=$true)][string]$RemoteTemporaryDirectory,
    [Parameter(Mandatory=$true)][string]$ReportPath,
    [switch]$AllowReplaceCurrentSystem
)
$ErrorActionPreference='Stop'
if (-not $AllowReplaceCurrentSystem) { throw 'Use a saved dedicated system; explicitly acknowledge file switching.' }
. (Join-Path $PSScriptRoot 'verify-live-mcp.ps1') -Endpoint $Endpoint -AccessToken $AccessToken -ClientInstanceId $ClientInstanceId -SkipReadOnlyCalls
$rows=[Collections.Generic.List[object]]::new()
function Call([string]$name,[hashtable]$arguments=@{}) {
    $rpc=Invoke-ModernToolCall -Name $name -Arguments $arguments
    return ConvertFrom-McpToolResult $rpc
}
function Assert([bool]$condition,[string]$message) { if (-not $condition) { throw $message } }
function Check([string]$name,[scriptblock]$body) {
    try { $value=& $body; $rows.Add([pscustomobject]@{name=$name;passed=$true;result=$value}); Write-Host "[PASS] $name" }
    catch { $rows.Add([pscustomobject]@{name=$name;passed=$false;error=$_.Exception.Message}); Write-Host "[FAIL] $name : $($_.Exception.Message)" }
}
$original=Call 'zemax_get_system'
Assert (-not $original.needsSave -and $original.fileExists -and -not [string]::IsNullOrWhiteSpace($original.filePath)) 'Active system is unsaved or has no recoverable on-disk file; refusing replacement.'
$temp=Join-Path $RemoteTemporaryDirectory ('ZemaxMCP-engineering-'+[Guid]::NewGuid().ToString('N')+'.zmx')
$switched=$false
$restored=$false
try {
    $switched=$true
    Call 'zemax_open_file' @{filePath=$SequentialFixture} | Out-Null
    Call 'zemax_save_file' @{filePath=$temp} | Out-Null
    $system=Call 'zemax_get_system'
    Assert ($system.systemMode -eq 'Sequential' -and $system.numberOfSurfaces -gt 4 -and $system.filePath -eq $temp) 'Dedicated sequential copy was not opened correctly.'
    $last=[int]$system.numberOfSurfaces-1
    Check 'summary-and-baseline-comparison' {
        $summary=Call 'zemax_system_summary'
        $again=Call 'zemax_system_summary' @{baselineSummaryJson=($summary | ConvertTo-Json -Depth 70 -Compress)}
        Assert ($null -ne $again.baselineComparison) 'Missing baseline comparison.'
        $again
    }
    Check 'purpose-preflight-and-workflow-plan' {
        $v=Call 'zemax_validate_model' @{purpose='imaging'}
        $p=Call 'zemax_task_plan' @{task='clipping'}
        @{validation=$v;plan=$p}
    }
    Check 'native-pupil-cross-tool-agreement' {
        $energy=Call 'zemax_energy_budget' @{fields=@(@(0.0,0.0),@(0.0,0.5)); wavelengths=@(1);gridSize=11;finalSurface=$last}
        Assert (@($energy.cases).Count -eq 2) 'Energy field case count differs.'
        foreach ($case in $energy.cases) {
            foreach ($surface in $case.surfaces) {
                Assert (($surface.clearRays+$surface.vignettedRays+$surface.traceErrorRays) -eq $surface.totalPupilSamples) 'Ray categories do not conserve sample count.'
            }
        }
        $fp=Call 'zemax_ray_footprint' @{surfaces=@($last);gridSize=11;maxPointsPerSurface=8;includeGlobalCoordinates=$true}
        $ap=Call 'zemax_aperture_throughput' @{surface=$last;gridSize=11}
        $diag=Call 'zemax_diagnose_clipping' @{surfaces=@($last);gridSize=11;maxPointsPerSurface=0}
        Assert ($fp.surfaces[0].pupilRays -eq $energy.numberOfPupilSamples -and $fp.surfaces[0].pupilRays -eq $ap.totalPupilRays) 'Pupil counts disagree.'
        Assert ($fp.surfaces[0].clearRays -eq $ap.clearRays -and $diag.clearRays -eq $ap.clearRays -and $diag.vignettedRays -eq $ap.vignettedRays -and $diag.rayTraceErrors -eq $ap.errorRays) 'Native clear/vignetted/error counts disagree.'
        $explain=Call 'zemax_explain_result' @{toolName='zemax_energy_budget';resultJson=($energy | ConvertTo-Json -Depth 70 -Compress)}
        @{energy=$energy;footprint=$fp;aperture=$ap;diagnosis=$diag;explanation=$explain}
    }
    Check 'native-coating-api-capability' {
        $rpc=Invoke-ModernToolCall -Name 'zemax_coating_rta' -Arguments @{surfaces=@(1,2);anglesDegrees=@(0,30);wavelengths=@(1)}
        $text=Get-ToolText $rpc
        $value=$text | ConvertFrom-Json
        if ($rpc.result.isError -or $value.success -eq $false) {
            Assert ($text -match 'unsupported|unavailable|not available|not expose|not support|requires|does not expose') 'Coating API failed without a clear capability explanation.'
            @{status='Unsupported-on-this-runtime';nativeReadback=$value}
        } else {
            Assert (@($value.samples).Count -eq 4) 'Native coating sample count differs.'
            foreach($sample in $value.samples) {
                Assert ($sample.rta.passiveRange -eq $true) 'Native coating returned nonpassive coefficients.'
            }
            $value
        }
    }
    Check 'mechanical-outline-contrasting-boundaries' {
        $inside=Call 'zemax_ray_footprint' @{surfaces=@($last);gridSize=11;mechanicalRectangle=@(-1.0,-1.0,1.0,1.0)}
        $outside=Call 'zemax_ray_footprint' @{surfaces=@($last);gridSize=11;mechanicalRectangle=@(1.0,1.0,2.0,2.0)}
        Assert ($inside.surfaces[0].userMechanicalBoundary.inside -eq 81 -and $inside.surfaces[0].userMechanicalBoundary.outside -eq 0) 'Enclosing outline did not accept all 81 clear ray samples.'
        Assert ($outside.surfaces[0].userMechanicalBoundary.outside -eq 81 -and $outside.surfaces[0].userMechanicalBoundary.inside -eq 0) 'Displaced outline did not exclude all 81 samples.'
        @{enclosing=$inside.surfaces[0].userMechanicalBoundary;displaced=$outside.surfaces[0].userMechanicalBoundary}
    }
    Check 'bounded-invalid-inputs' {
        foreach ($entry in @(
            @{name='zemax_ray_footprint';args=@{gridSize=100}},
            @{name='zemax_coating_rta';args=@{surfaces=@(1);anglesDegrees=@(90)}},
            @{name='zemax_validate_model';args=@{purpose='not-a-purpose'}},
            @{name='zemax_audit_native_zrd';args=@{zrdPath='relative.ZRD'}}
        )) {
            $rpc=Invoke-ModernToolCall -Name $entry.name -Arguments $entry.args
            $value=Get-ToolPayload $rpc
            Assert ($rpc.result.isError -or $value.success -eq $false) "Invalid input unexpectedly accepted by $($entry.name)."
        }
        'Four malformed inputs rejected; no arbitrary file reads.'
    }
    Check 'read-only-analysis-save-state' {
        $after=Call 'zemax_get_system'
        Assert (-not $after.needsSave -and $after.filePath -eq $temp -and $after.numberOfSurfaces -eq $system.numberOfSurfaces) 'Read-only analyses changed lens save state, path or surface count.'
        @{filePath=$after.filePath;needsSave=$after.needsSave;surfaces=$after.numberOfSurfaces}
    }
    if (-not [string]::IsNullOrWhiteSpace($NscFixture)) {
        Call 'zemax_open_file' @{filePath=$NscFixture} | Out-Null
        $nscCopy=Join-Path $RemoteTemporaryDirectory ('ZemaxMCP-nsc-'+[Guid]::NewGuid().ToString('N')+'.zmx')
        Call 'zemax_save_file' @{filePath=$nscCopy} | Out-Null
        Check 'nsc-same-trace-detector-cross-check' {
            $scene=Call 'zemax_nsc_scene_summary'
            $objects=Call 'zemax_get_nsc_objects' @{maxObjects=100}
            $detectors=@($objects.objects | Where-Object { $_.isDetector } | ForEach-Object { [int]$_.number })
            Assert ($detectors.Count -gt 0 -and $detectors.Count -le 16) 'Fixture needs 1..16 scalar detectors.'
            $trace=Call 'zemax_run_nsc_ray_trace' @{clearDetectors=$true;detectorObject=0;splitRays=$true;scatterRays=$false;usePolarization=$true;ignoreErrors=$false;timeoutSeconds=30.0;runInBackground=$false;snapshotDetectorObjects=$detectors;includeConfiguredSourcePower=$true}
            $ledger=$trace.sameTraceEnergy
            Assert ($trace.state -eq 'Completed' -and $ledger.sameTraceCaptured -and $ledger.detectorsClearedBeforeTrace -and -not [string]::IsNullOrWhiteSpace($ledger.traceCaptureId)) 'Missing completed same-trace provenance.'
            Assert ($ledger.additiveSourceToDetectorBalanceValid -eq $false -and $null -eq $ledger.userDeclaredLaunchedFlux -and $null -eq $ledger.unassignedEnergy) 'Unmeasured power/loss was assigned an invented denominator or energy closure.'
            $budget=Call 'zemax_nsc_energy_budget' @{detectorObjects=$detectors}
            $reads=@()
            $positive=$false
            foreach($reading in $ledger.detectors) {
                $id=[int]$reading.objectNumber
                $det=Call 'zemax_get_nsc_detector' @{objectNumber=$id;includePixels=$true;rowCount=2;columnCount=2;dataType=0}
                $reference=@($budget.detectors | Where-Object { $_.objectNumber -eq $id })[0]
                $tol=1e-9*[Math]::Max(1.0,[Math]::Abs([double]$reading.incidentFlux))
                Assert ([Math]::Abs([double]$reading.incidentFlux-[double]$det.totalIncidentFlux) -le $tol -and [Math]::Abs([double]$reading.incidentFlux-[double]$reference.incidentFlux) -le $tol) 'Same-trace/native detector/energy budget flux disagree.'
                Assert (@($det.roiPixels).Count -eq 2 -and @($det.roiPixels[0]).Count -eq 2) 'Requested detector ROI shape missing.'
                $sum=0.0
                foreach($row in $det.roiPixels) { foreach($v in $row) { $sum += [double]$v } }
                Assert ([Math]::Abs($sum-[double]$det.roiFluxSum) -le 1e-9*[Math]::Max(1.0,[Math]::Abs($sum))) 'Native ROI sum inconsistent.'
                if ([double]$reading.incidentFlux -gt 0) { $positive=$true }
                $reads+=$det
            }
            Assert $positive 'Every detector returned zero flux; cannot treat that as a meaningful power readback test.'
            @{scene=$scene;trace=$trace;budget=$budget;detectorReads=$reads;workingCopy=$nscCopy}
        }
        if (-not [string]::IsNullOrWhiteSpace($NativeZrdFixture)) {
            Check 'native-zrd-topology-readback' {
                $zrd=Call 'zemax_audit_native_zrd' @{zrdPath=$NativeZrdFixture;timeoutSeconds=30.0}
                Assert ($zrd.nativeRayRecords -gt 0 -and $zrd.nativeSegmentRecords -gt 0 -and $zrd.zrdSha256 -match '^[a-fA-F0-9]{64}$') 'Native ZRD reader returned no data or digest.'
                $zrd
            }
        }
    }
} finally {
    if ($switched) {
        try {
            Call 'zemax_open_file' @{filePath=$original.filePath} | Out-Null
            $back=Call 'zemax_get_system'
            Assert ($back.filePath -eq $original.filePath -and -not $back.needsSave) 'Original file restoration not confirmed.'
            $restored=$true
            Write-Host '[PASS] original-file-restored'
        } catch { $rows.Add([pscustomobject]@{name='original-file-restored';passed=$false;error=$_.Exception.Message}); Write-Warning $_.Exception.Message }
    }
    [ordered]@{testedAt=[DateTimeOffset]::Now.ToString('o');endpoint=$Endpoint;clientInstanceId=$script:clientInstanceId;manifestFingerprint=$health.manifestFingerprint;fixture=$SequentialFixture;temporaryCopy=$temp;originalFile=$original.filePath;originalRestored=$restored;checks=$rows} | ConvertTo-Json -Depth 90 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
if (@($rows | Where-Object { -not $_.passed }).Count) { throw "Engineering acceptance failed; see $ReportPath" }
Write-Host "Engineering acceptance report: $ReportPath"
