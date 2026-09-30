[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FixturePath,
    [string]$Endpoint = "http://127.0.0.1:8000/mcp",
    [string]$AccessToken = $env:ZEMAX_MCP_TOKEN,
    [string]$ReportPath = "",
    [switch]$VerifyOptimization,
    [switch]$VerifyBackgroundJobs,
    [switch]$VerifyNsc,
    [switch]$VerifyTolerance,
    [int]$JobWaitSeconds = 90,
    [switch]$KeepWorkingCopy
)

$ErrorActionPreference = "Stop"
$endpointUri = $Endpoint.TrimEnd("/")
$fixture = [IO.Path]::GetFullPath($FixturePath)
if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw "Fixture file not found: $fixture" }
$extension = [IO.Path]::GetExtension($fixture)
if ($extension -notin @(".zmx", ".zos")) { throw "FixturePath must be a .zmx or .zos file." }
if ($JobWaitSeconds -lt 10 -or $JobWaitSeconds -gt 1800) { throw "JobWaitSeconds must be between 10 and 1800." }

$runRoot = Join-Path ([IO.Path]::GetTempPath()) ("ZemaxMCP-live-functional-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$workingCopy = Join-Path $runRoot ("fixture-live" + $extension)
Copy-Item -LiteralPath $fixture -Destination $workingCopy -Force

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path (Get-Location) ("live-functional-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)

$script:nextId = 1
$script:clientInstanceId = "functional-" + [Guid]::NewGuid().ToString("N")
$script:protocolVersion = "2026-07-28"
$results = [System.Collections.Generic.List[object]]::new()

function ConvertFrom-McpResponse {
    param($Response)
    $content = [string]$Response.Content
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    if ([string]$Response.Headers["Content-Type"] -match "text/event-stream" -or $content -match "(?m)^data:") {
        $line = @($content -split '[\r\n]+' | Where-Object { $_ -match '^data:' } | Select-Object -First 1)
        if ($line.Count -eq 0) { throw "MCP SSE response contained no data payload." }
        $content = ([string]$line[0]).Substring(5).Trim()
    }
    return $content | ConvertFrom-Json
}

function New-Meta {
    return @{
        "io.modelcontextprotocol/protocolVersion" = $script:protocolVersion
        "io.modelcontextprotocol/clientInfo" = @{ name = "zemax-mcp-functional-verifier"; version = "1.0" }
        "io.modelcontextprotocol/clientCapabilities" = @{}
        "io.zemaxmcp/clientInstanceId" = $script:clientInstanceId
    }
}

function Invoke-Mcp {
    param([string]$Method, [hashtable]$Params = @{}, [string]$ToolName)
    $wire = @{}
    foreach ($key in $Params.Keys) { $wire[$key] = $Params[$key] }
    $wire["_meta"] = New-Meta
    $payload = @{ jsonrpc = "2.0"; id = $script:nextId++; method = $Method; params = $wire } | ConvertTo-Json -Depth 60 -Compress
    $headers = @{
        Accept = "application/json, text/event-stream"
        "MCP-Protocol-Version" = $script:protocolVersion
        "Mcp-Method" = $Method
        "X-Zemax-MCP-Client-Instance" = $script:clientInstanceId
    }
    if ($ToolName) { $headers["Mcp-Name"] = $ToolName }
    if ($AccessToken) { $headers.Authorization = "Bearer $AccessToken" }
    $response = Invoke-WebRequest -UseBasicParsing -Uri $endpointUri -Method Post -Headers $headers -ContentType "application/json" -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 180
    $json = ConvertFrom-McpResponse $response
    if ($json.error) { throw "$Method JSON-RPC error $($json.error.code): $($json.error.message)" }
    return $json
}

function Invoke-Tool {
    param([string]$Name, [hashtable]$Arguments = @{})
    return Invoke-Mcp -Method "tools/call" -ToolName $Name -Params @{ name = $Name; arguments = $Arguments }
}

function Get-ToolPayload {
    param($Response)
    $text = [string](@($Response.result.content | Where-Object { $_.type -eq "text" } | Select-Object -First 1).text)
    if ([string]::IsNullOrWhiteSpace($text)) { throw "Tool returned no text payload." }
    $payload = $text | ConvertFrom-Json
    if ($Response.result.isError -eq $true) {
        $reason = if ($payload.error) { [string]$payload.error } else { $text }
        throw "MCP isError=true: $reason"
    }
    if ($payload.PSObject.Properties.Name -contains "success" -and $payload.success -eq $false) {
        throw "Tool success=false: $($payload.error)"
    }
    return $payload
}

function Add-Result {
    param([string]$Name, [string]$Status, [string]$Detail = "")
    $results.Add([pscustomobject]@{ name = $Name; status = $Status; detail = $Detail; at = [DateTimeOffset]::UtcNow }) | Out-Null
    $suffix = if ($Detail) { ": " + $Detail } else { "" }
    Write-Host "[$Status] $Name$suffix"
}

function Invoke-Check {
    param([string]$Name, [scriptblock]$Action, [switch]$Optional)
    try {
        $detail = & $Action
        Add-Result $Name "PASS" ([string]$detail)
        return $true
    }
    catch {
        if ($Optional) {
            Add-Result $Name "SKIPPED" $_.Exception.Message
            return $false
        }
        Add-Result $Name "FAIL" $_.Exception.Message
        return $false
    }
}

function Get-Health {
    $headers = @{}
    if ($AccessToken) { $headers.Authorization = "Bearer $AccessToken" }
    return Invoke-RestMethod -Uri ($endpointUri + "/health") -Headers $headers -TimeoutSec 30
}

$healthBefore = $null
$tools = @()
$systemMode = ""
$surfaceCountBefore = 0

try {
    Invoke-Check "static-discovery" {
        $list = Invoke-Mcp -Method "tools/list"
        $script:tools = @($list.result.tools | ForEach-Object { $_.name })
        if ($script:tools.Count -eq 0) { throw "tools/list returned no tools." }
        "$($script:tools.Count) tools"
    } | Out-Null

    Invoke-Check "health-contract" {
        $script:healthBefore = Get-Health
        if (-not $script:healthBefore.bridgeRunning -or -not $script:healthBefore.mcpServerRunning) { throw "Host/Worker is not healthy." }
        if ($script:healthBefore.rpcVersion -ne $script:healthBefore.workerRpcVersion) { throw "Host/Worker RPC mismatch." }
        "RPC=$($script:healthBefore.rpcVersion), toolset=$($script:healthBefore.toolset)"
    } | Out-Null

    Invoke-Check "open-temp-fixture" {
        if ("zemax_open_file" -notin $script:tools) { throw "Active profile does not expose zemax_open_file." }
        $opened = Get-ToolPayload (Invoke-Tool "zemax_open_file" @{ filePath = $workingCopy })
        if (-not $opened.filePath) { throw "Open result did not report the active file." }
        [IO.Path]::GetFileName([string]$opened.filePath)
    } | Out-Null

    Invoke-Check "read-system-baseline" {
        if ("zemax_get_system" -in $script:tools) {
            $system = Get-ToolPayload (Invoke-Tool "zemax_get_system")
            $script:systemMode = [string]$system.systemMode
            $script:surfaceCountBefore = [int]$system.numberOfSurfaces
            return "mode=$($script:systemMode), surfaces=$($script:surfaceCountBefore)"
        }
        if ("zemax_get_nonsequential_system_settings" -in $script:tools) {
            $nscSettings = Get-ToolPayload (Invoke-Tool "zemax_get_nonsequential_system_settings")
            $script:systemMode = [string]$nscSettings.systemMode
            $script:surfaceCountBefore = 0
            return "mode=$($script:systemMode), NSC-focused profile"
        }
        throw "Active profile exposes neither zemax_get_system nor zemax_get_nonsequential_system_settings."
    } | Out-Null

    if ($systemMode -notmatch "NonSequential" -and "zemax_get_system" -in $tools) {
        $script:addedSurfaces = @()
        $mutationTools = @("zemax_add_surface", "zemax_set_surface", "zemax_batch_set_surfaces", "zemax_remove_surface")
        $missingMutation = @($mutationTools | Where-Object { $_ -notin $tools })
        if ($missingMutation.Count -gt 0) {
            Add-Result "sequential-edit-readback" "SKIPPED" ("Active profile omits: " + ($missingMutation -join ", "))
        }
        else {
            $editOk = Invoke-Check "sequential-edit-readback" {
                $addedA = Get-ToolPayload (Invoke-Tool "zemax_add_surface" @{ insertAt = 0; radius = 0.0; thickness = 1.0; comment = "ZemaxMCP live A" })
                $script:addedSurfaces += [int]$addedA.surfaceNumber
                if ([int]$addedA.totalSurfaces -ne $surfaceCountBefore + 1) { throw "First surface count did not increase by one." }

                $addedB = Get-ToolPayload (Invoke-Tool "zemax_add_surface" @{ insertAt = 0; radius = 0.0; thickness = 2.0; comment = "ZemaxMCP live B" })
                $script:addedSurfaces += [int]$addedB.surfaceNumber
                if ([int]$addedB.totalSurfaces -ne $surfaceCountBefore + 2) { throw "Second surface count did not increase by one." }

                $first = [int]$script:addedSurfaces[0]
                $second = [int]$script:addedSurfaces[1]
                Get-ToolPayload (Invoke-Tool "zemax_set_surface" @{
                    surfaceNumber = $first; thickness = 1.1; comment = "ZemaxMCP live single-set"
                }) | Out-Null

                $batch = Get-ToolPayload (Invoke-Tool "zemax_batch_set_surfaces" @{
                    edits = @(
                        @{ surfaceNumber = $first; thickness = 1.25; comment = "ZemaxMCP batch A" },
                        @{ surfaceNumber = $second; thickness = 2.5; comment = "ZemaxMCP batch B" }
                    )
                })
                if ([int]$batch.appliedEdits -ne 2 -or $batch.rolledBack -eq $true) {
                    throw "Batch surface edit did not report two committed edits."
                }

                $readback = Get-ToolPayload (Invoke-Tool "zemax_get_system")
                $matchA = @($readback.surfaces | Where-Object { [int]$_.number -eq $first } | Select-Object -First 1)
                $matchB = @($readback.surfaces | Where-Object { [int]$_.number -eq $second } | Select-Object -First 1)
                if ($matchA.Count -ne 1 -or [Math]::Abs([double]$matchA[0].thickness - 1.25) -gt 1e-9) {
                    throw "Independent GetSystem readback did not observe batch edit A."
                }
                if ($matchB.Count -ne 1 -or [Math]::Abs([double]$matchB[0].thickness - 2.5) -gt 1e-9) {
                    throw "Independent GetSystem readback did not observe batch edit B."
                }

                foreach ($surfaceNumber in @($script:addedSurfaces | Sort-Object -Descending)) {
                    Get-ToolPayload (Invoke-Tool "zemax_remove_surface" @{ surfaceNumber = [int]$surfaceNumber }) | Out-Null
                }
                $script:addedSurfaces = @()
                $final = Get-ToolPayload (Invoke-Tool "zemax_get_system")
                if ([int]$final.numberOfSurfaces -ne $surfaceCountBefore) { throw "Surface count did not return to baseline." }
                "add/single-set/batch-set/readback/remove verified"
            }
            if (-not $editOk -and $script:addedSurfaces.Count -gt 0) {
                foreach ($surfaceNumber in @($script:addedSurfaces | Sort-Object -Descending)) {
                    try { Get-ToolPayload (Invoke-Tool "zemax_remove_surface" @{ surfaceNumber = [int]$surfaceNumber }) | Out-Null } catch { }
                }
                $script:addedSurfaces = @()
            }
        }

        $snapshotTools = @("zemax_snapshot_list", "zemax_snapshot_diff", "zemax_snapshot_restore")
        if (@($snapshotTools | Where-Object { $_ -notin $tools }).Count -eq 0) {
            Invoke-Check "snapshot-list-diff-restore" {
                $latestPath = [string](Get-Health).lastSnapshotPath
                if ([string]::IsNullOrWhiteSpace($latestPath)) { throw "Sequential mutation checks did not report a safety snapshot." }
                $snapshotName = [IO.Path]::GetFileName($latestPath)

                $listed = Get-ToolPayload (Invoke-Tool "zemax_snapshot_list" @{ limit = 10 })
                if ($snapshotName -notin @($listed.snapshots | ForEach-Object { $_.fileName })) {
                    throw "Latest safety snapshot was not returned by zemax_snapshot_list."
                }

                $diff = Get-ToolPayload (Invoke-Tool "zemax_snapshot_diff" @{
                    snapshotFileName = $snapshotName
                    maxDifferences = 20
                    maxSurfaces = 200
                })
                if ([int]$diff.inspectedSurfaces -lt 1) { throw "Snapshot diff did not inspect any sequential surfaces." }

                $restoredPath = $null
                try {
                    $restore = Get-ToolPayload (Invoke-Tool "zemax_snapshot_restore" @{ snapshotFileName = $snapshotName })
                    $restoredPath = [string]$restore.workingFilePath
                    if ([string]::IsNullOrWhiteSpace([string]$restore.protectedCurrentSnapshotFileName) -or
                        [string]::IsNullOrWhiteSpace($restoredPath) -or
                        -not (Test-Path -LiteralPath $restoredPath)) {
                        throw "Controlled restore did not protect the current state and open a separate working copy."
                    }
                }
                finally {
                    try { Get-ToolPayload (Invoke-Tool "zemax_open_file" @{ filePath = $workingCopy }) | Out-Null } catch { }
                    if (-not [string]::IsNullOrWhiteSpace($restoredPath)) {
                        try { if (Test-Path -LiteralPath $restoredPath) { Remove-Item -LiteralPath $restoredPath -Force } } catch { }
                    }
                }

                "snapshot=$snapshotName, observedDiffs=$($diff.observedPropertyDifferences)"
            } | Out-Null
        }

        if ("zemax_ray_trace_diagnostics" -in $tools) {
            Invoke-Check "sequential-ray-diagnostics" {
                $diagnostics = Get-ToolPayload (Invoke-Tool "zemax_ray_trace_diagnostics" @{
                    fieldSampling = 1; pupilSampling = 5; allWavelengths = $false; surface = 0; maxFailures = 10
                })
                if ([int]$diagnostics.totalRays -ne 5) { throw "Expected five diagnostic rays for 1x5 sampling." }
                if ([int]$diagnostics.clearRays -gt [int]$diagnostics.validRays -or
                    [int]$diagnostics.validRays -gt [int]$diagnostics.totalRays) {
                    throw "Ray diagnostic counts are internally inconsistent."
                }
                "total=$($diagnostics.totalRays), clear=$($diagnostics.clearRays), problems=$($diagnostics.problemRays)"
            } | Out-Null
        }

        if ("zemax_cardinal_points" -in $tools) {
            Invoke-Check "sequential-analysis-cardinal-points" {
                Get-ToolPayload (Invoke-Tool "zemax_cardinal_points") | Out-Null
                "structured result returned"
            } | Out-Null
        }

        if ($VerifyOptimization) {
            Invoke-Check "local-optimization-one-cycle" {
                if ("zemax_optimize" -notin $tools) { throw "Active profile does not expose zemax_optimize." }
                $payload = Get-ToolPayload (Invoke-Tool "zemax_optimize" @{ algorithm = "DLS"; cycles = 1 })
                "termination=$($payload.terminationReason)"
            } | Out-Null
        }

        if ($VerifyBackgroundJobs) {
            Invoke-Check "background-job-lifecycle" {
                foreach ($required in @("zemax_global_search", "zemax_job_status", "zemax_job_cancel")) {
                    if ($required -notin $tools) { throw "Active profile does not expose $required." }
                }
                $started = Get-ToolPayload (Invoke-Tool "zemax_global_search" @{ algorithm = "DLS"; cores = 0; solutionsToSave = 10; timeoutSeconds = 10.0; runInBackground = $true })
                $jobId = [string]$started.jobId
                if ([string]::IsNullOrWhiteSpace($jobId)) { throw "Global search did not return a jobId." }

                Start-Sleep -Milliseconds 500
                Get-ToolPayload (Invoke-Tool "zemax_job_cancel" @{ jobId = $jobId }) | Out-Null
                $deadline = [DateTime]::UtcNow.AddSeconds($JobWaitSeconds)
                $status = $null
                do {
                    Start-Sleep -Milliseconds 500
                    $status = Get-ToolPayload (Invoke-Tool "zemax_job_status" @{ jobId = $jobId })
                    if ($status.state -in @("Completed", "Cancelled", "Failed")) { break }
                } while ([DateTime]::UtcNow -lt $deadline)
                if ($null -eq $status -or $status.state -notin @("Completed", "Cancelled", "Failed")) { throw "Job $jobId did not become terminal within $JobWaitSeconds seconds." }
                if ([string]::IsNullOrWhiteSpace([string]$status.parentOperationId)) { throw "Job status did not preserve ParentOperationId." }
                "job=$jobId parentOperationId=$($status.parentOperationId) state=$($status.state)"
            } | Out-Null
        }
    }

    if ($VerifyNsc) {
        Invoke-Check "nsc-structural-summary" {
            if ($systemMode -notmatch "NonSequential") { throw "Fixture is not a non-sequential system." }
            foreach ($required in @("zemax_nsc_scene_summary", "zemax_get_nsc_objects", "zemax_run_nsc_ray_trace")) {
                if ($required -notin $tools) { throw "Active profile does not expose $required." }
            }
            $summary = Get-ToolPayload (Invoke-Tool "zemax_nsc_scene_summary")
            $objects = Get-ToolPayload (Invoke-Tool "zemax_get_nsc_objects" @{ startObject = 1; maxObjects = 100 })
            $trace = Get-ToolPayload (Invoke-Tool "zemax_run_nsc_ray_trace" @{
                clearDetectors = $true
                detectorObject = 0
                splitRays = $false
                scatterRays = $false
                usePolarization = $false
                ignoreErrors = $true
                timeoutSeconds = 60.0
                runInBackground = $false
            })
            if ($trace.state -ne "Completed") { throw "NSC ray trace did not complete." }
            "objects=$($summary.numberOfObjects), detectors=$($summary.detectorObjects), returned=$(@($objects.objects).Count), traceSeconds=$($trace.runtimeSeconds)"
        } | Out-Null
    }

    if ($VerifyTolerance) {
        Invoke-Check "tolerance-structural-and-live-run" {
            foreach ($required in @("zemax_tolerance_summary", "zemax_get_tolerances", "zemax_run_tolerancing")) {
                if ($required -notin $tools) { throw "Active profile does not expose $required." }
            }
            $summary = Get-ToolPayload (Invoke-Tool "zemax_tolerance_summary" @{ maxOperands = 500 })
            Get-ToolPayload (Invoke-Tool "zemax_get_tolerances" @{ startRow = 1; maxOperands = 20 }) | Out-Null
            if ([int]$summary.numberOfOperands -lt 1) { throw "Tolerance fixture contains no TDE operands." }

            $run = Get-ToolPayload (Invoke-Tool "zemax_run_tolerancing" @{
                includeSensitivity = $true
                criterion = "RMSSpotRadius"
                criterionSampling = 3
                criterionComp = "None"
                criterionCycle = 1
                criterionField = "UserDefined"
                monteCarloRuns = 3
                monteCarloStatistic = "Normal"
                maxSensitivityOperands = 10
                timeoutSeconds = 120.0
                runInBackground = $false
            })
            if ($run.state -ne "Completed") { throw "Tolerancing run did not complete." }
            if ([int]$run.monteCarloRows -lt 1 -or [int]$run.monteCarloColumns -lt 1) {
                throw "Tolerancing returned no structured Monte Carlo matrix."
            }
            if ([int]$run.sensitivityCriteria -lt 1) {
                throw "Sensitivity mode returned no structured sensitivity criteria."
            }
            "operands=$($summary.numberOfOperands), MC=$($run.monteCarloRows)x$($run.monteCarloColumns), sensitivityCriteria=$($run.sensitivityCriteria)"
        } | Out-Null
    }

    Invoke-Check "save-temp-fixture" {
        if ("zemax_save_file" -notin $tools) { throw "Active profile does not expose zemax_save_file." }
        $saved = Get-ToolPayload (Invoke-Tool "zemax_save_file")
        if (-not (Test-Path -LiteralPath ([string]$saved.filePath))) { throw "SaveFile did not produce a file." }
        [string]$saved.filePath
    } | Out-Null
}
finally {
    $healthAfter = $null
    try { $healthAfter = Get-Health } catch { }
    $report = [ordered]@{
        generatedAt = [DateTimeOffset]::UtcNow
        endpoint = $endpointUri
        fixture = $fixture
        workingCopy = $workingCopy
        protocolVersion = $script:protocolVersion
        toolCount = $tools.Count
        toolset = if ($healthBefore) { $healthBefore.toolset } else { $null }
        rpcVersion = if ($healthBefore) { $healthBefore.rpcVersion } else { $null }
        manifestFingerprint = if ($healthBefore) { $healthBefore.manifestFingerprint } else { $null }
        licenseStatus = if ($healthBefore) { $healthBefore.licenseStatus } else { $null }
        workerGenerationBefore = if ($healthBefore) { $healthBefore.worker.workerGeneration } else { $null }
        workerGenerationAfter = if ($healthAfter) { $healthAfter.worker.workerGeneration } else { $null }
        tests = $results
    }
    $reportDirectory = Split-Path -Parent $ReportPath
    if ($reportDirectory) { New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null }
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    Write-Host "Functional live report: $ReportPath"

    if (-not $KeepWorkingCopy) {
        try { Remove-Item -LiteralPath $runRoot -Recurse -Force } catch { }
    }
}

$failures = @($results | Where-Object { $_.status -eq "FAIL" })
if ($failures.Count -gt 0) { throw "Functional live verification failed in $($failures.Count) check(s). See $ReportPath." }
Write-Host "Functional live verification completed successfully."
