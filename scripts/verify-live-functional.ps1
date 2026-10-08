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
    [switch]$VerifyOfficialTasks,
    [switch]$VerifyTaskCancellation,
    [switch]$VerifyWorkerCrashRecovery,
    [switch]$AllowWorkerTermination,
    [int]$JobWaitSeconds = 90,
    [switch]$AllowReplaceCurrentSystem,
    [switch]$KeepWorkingCopy
)

$ErrorActionPreference = "Stop"
if (-not $AllowReplaceCurrentSystem) {
    throw "Functional live tests replace the currently open OpticStudio system. Save your work and use a dedicated validation instance; rerun with -AllowReplaceCurrentSystem to acknowledge the replacement."
}
$endpointUri = $Endpoint.TrimEnd("/")
$fixture = [IO.Path]::GetFullPath($FixturePath)
if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw "Fixture file not found: $fixture" }
$extension = [IO.Path]::GetExtension($fixture)
if ($extension -notin @(".zmx", ".zos")) { throw "FixturePath must be a .zmx or .zos file." }
if ($JobWaitSeconds -lt 10 -or $JobWaitSeconds -gt 1800) { throw "JobWaitSeconds must be between 10 and 1800." }
if ($VerifyTaskCancellation -and (-not $VerifyOfficialTasks -or -not $VerifyBackgroundJobs)) {
    throw "-VerifyTaskCancellation needs -VerifyOfficialTasks and -VerifyBackgroundJobs with a sequential fixture."
}
if ($VerifyOfficialTasks -and -not ($VerifyNsc -or $VerifyTolerance -or $VerifyBackgroundJobs)) {
    throw "-VerifyOfficialTasks needs a representative -VerifyNsc, -VerifyTolerance or -VerifyBackgroundJobs fixture."
}
if ($VerifyWorkerCrashRecovery -and (-not $VerifyOfficialTasks -or -not $AllowWorkerTermination -or
    -not ($VerifyNsc -or $VerifyBackgroundJobs))) {
    throw "Worker crash recovery requires -VerifyOfficialTasks, -AllowWorkerTermination and an NSC or global-search job. It forcibly terminates the licensed Worker process."
}

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
    param([switch]$TaskOptIn)
    $capabilities = @{}
    if ($TaskOptIn) {
        $capabilities["extensions"] = @{ "io.modelcontextprotocol/tasks" = @{} }
    }
    return @{
        "io.modelcontextprotocol/protocolVersion" = $script:protocolVersion
        "io.modelcontextprotocol/clientInfo" = @{ name = "zemax-mcp-functional-verifier"; version = "1.0" }
        "io.modelcontextprotocol/clientCapabilities" = $capabilities
        "io.zemaxmcp/clientInstanceId" = $script:clientInstanceId
    }
}

function Invoke-Mcp {
    param([string]$Method, [hashtable]$Params = @{}, [string]$ToolName, [switch]$TaskOptIn)
    $wire = @{}
    foreach ($key in $Params.Keys) { $wire[$key] = $Params[$key] }
    $wire["_meta"] = New-Meta -TaskOptIn:$TaskOptIn
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
    param([string]$Name, [hashtable]$Arguments = @{}, [switch]$TaskOptIn)
    return Invoke-Mcp -Method "tools/call" -ToolName $Name -Params @{ name = $Name; arguments = $Arguments } -TaskOptIn:$TaskOptIn
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


function Start-OfficialTask {
    param([string]$Name, [hashtable]$Arguments)
    $result = Invoke-Tool -Name $Name -Arguments $Arguments -TaskOptIn
    if ($result.result.resultType -ne "task" -or [string]::IsNullOrWhiteSpace([string]$result.result.taskId)) {
        throw "$Name did not return a Task. Start the Host with --enable-official-tasks true and use MCP 2026-07-28."
    }
    if ($result.result.status -ne "working") {
        throw "Task creation unexpectedly reported an immediate terminal state."
    }
    return [string]$result.result.taskId
}

function Get-OfficialTask {
    param([string]$TaskId)
    return (Invoke-Mcp -Method "tasks/get" -ToolName $TaskId -Params @{ taskId = $TaskId } -TaskOptIn).result
}

function Wait-OfficialTask {
    param([string]$TaskId, [int]$TimeoutSeconds = $JobWaitSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $current = Get-OfficialTask $TaskId
        if ($current.status -in @("completed", "failed", "cancelled")) { return $current }
        if ($current.status -notin @("working", "input_required")) {
            throw "Unknown Task status: $($current.status)"
        }
        if ($current.status -eq "input_required") { throw "ZOS-API Jobs must not require interactive Task input." }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Task $TaskId remained non-terminal after $TimeoutSeconds seconds."
}

function Assert-RealTaskCompletion {
    param([string]$TaskId, [string]$ToolName)
    $terminal = Wait-OfficialTask $TaskId
    if ($terminal.status -ne "completed") {
        throw "$ToolName Task ended as '$($terminal.status)': $($terminal.statusMessage)"
    }
    # This must contain the actual Worker result, not a synthetic Job ID.
    $tool = $terminal.result
    if ($null -eq $tool -or $tool.isError -eq $true -or @($tool.content).Count -ne 1) {
        throw "$ToolName Task contained no successful CallToolResult."
    }
    $content = [string]$tool.content[0].text
    if ([string]::IsNullOrWhiteSpace($content)) { throw "$ToolName Task result was empty." }
    try { $parsed = $content | ConvertFrom-Json }
    catch { throw "$ToolName Task result could not be decoded as a real structured Worker payload." }
    if ($parsed.PSObject.Properties.Name -contains "jobId") {
        throw "$ToolName Task returned only a Job ID instead of the final operation result."
    }
    return $parsed
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
$script:openAttempted = $false

try {
    if (-not (Invoke-Check "static-discovery" {
        $list = Invoke-Mcp -Method "tools/list"
        $script:tools = @($list.result.tools | ForEach-Object { $_.name })
        if ($script:tools.Count -eq 0) { throw "tools/list returned no tools." }
        "$($script:tools.Count) tools"
    })) { throw "Aborting functional acceptance: MCP static discovery failed." }

    if (-not (Invoke-Check "health-contract" {
        $script:healthBefore = Get-Health
        if (-not $script:healthBefore.bridgeRunning -or -not $script:healthBefore.mcpServerRunning) { throw "Host/Worker is not healthy." }
        if ($script:healthBefore.rpcVersion -ne $script:healthBefore.workerRpcVersion) { throw "Host/Worker RPC mismatch." }
        if ([string]::IsNullOrWhiteSpace([string]$script:healthBefore.manifestFingerprint) -or
            $script:healthBefore.manifestFingerprint -ne $script:healthBefore.workerManifestFingerprint) {
            throw "Host/Worker tool manifest fingerprint mismatch."
        }
        if ($script:healthBefore.readOnly) { throw "Functional acceptance needs a read/write Host profile." }
        "RPC=$($script:healthBefore.rpcVersion), toolset=$($script:healthBefore.toolset)"
    })) { throw "Aborting functional acceptance: Host/Worker health or contract validation failed." }

    if (-not (Invoke-Check "open-temp-fixture" {
        if ("zemax_open_file" -notin $script:tools) { throw "Active profile does not expose zemax_open_file." }
        $script:openAttempted = $true
        $opened = Get-ToolPayload (Invoke-Tool "zemax_open_file" @{ filePath = $workingCopy })
        if ([string]::IsNullOrWhiteSpace([string]$opened.filePath) -or
            -not [string]::Equals([IO.Path]::GetFullPath([string]$opened.filePath), $workingCopy, [StringComparison]::OrdinalIgnoreCase)) {
            throw "OpenFile did not confirm the temporary fixture as the active optical system."
        }
        [IO.Path]::GetFileName([string]$opened.filePath)
    })) { throw "Aborting functional acceptance: test fixture was not opened. No mutation tests will be executed." }

    if (-not (Invoke-Check "read-system-baseline" {
        if ("zemax_get_system" -in $script:tools) {
            $system = Get-ToolPayload (Invoke-Tool "zemax_get_system")
            if ([string]::IsNullOrWhiteSpace([string]$system.filePath) -or
                -not [string]::Equals([IO.Path]::GetFullPath([string]$system.filePath), $workingCopy, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Independent GetSystem readback does not point to the temporary fixture."
            }
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
    })) { throw "Aborting functional acceptance: model identity or mode could not be verified." }

    if ($systemMode -notmatch "NonSequential" -and "zemax_get_system" -in $tools) {
        $script:addedSurfaces = @()
        $editOk = $false
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
        if ($editOk -and @($snapshotTools | Where-Object { $_ -notin $tools }).Count -eq 0) {
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
                    $reopenedFixture = $false
                    try {
                        $reopen = Get-ToolPayload (Invoke-Tool "zemax_open_file" @{ filePath = $workingCopy })
                        $reopenedFixture = -not [string]::IsNullOrWhiteSpace([string]$reopen.filePath) -and
                            [string]::Equals([IO.Path]::GetFullPath([string]$reopen.filePath), $workingCopy, [StringComparison]::OrdinalIgnoreCase)
                    }
                    catch {
                        Write-Warning "Could not reopen the validation fixture after snapshot restore: $($_.Exception.Message)"
                    }
                    if ($reopenedFixture -and -not [string]::IsNullOrWhiteSpace($restoredPath)) {
                        try { if (Test-Path -LiteralPath $restoredPath) { Remove-Item -LiteralPath $restoredPath -Force } } catch { }
                    }
                    elseif (-not [string]::IsNullOrWhiteSpace($restoredPath)) {
                        Write-Warning "Restored working file may still be open; it was retained at $restoredPath"
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


    if ($VerifyOfficialTasks) {
        if ($VerifyNsc) {
            Invoke-Check "official-task-nsc-real-completion" {
                if ($systemMode -notmatch "NonSequential") { throw "NSC Task completion requires an NSC fixture." }
                $taskId = Start-OfficialTask "zemax_run_nsc_ray_trace" @{
                    clearDetectors = $true; detectorObject = 0; splitRays = $false;
                    scatterRays = $false; usePolarization = $false; ignoreErrors = $true;
                    timeoutSeconds = 60.0; runInBackground = $true
                }
                $actual = Assert-RealTaskCompletion $taskId "zemax_run_nsc_ray_trace"
                "task=$taskId; finalState=$($actual.state); traceSeconds=$($actual.runtimeSeconds)"
            } | Out-Null
        }
        if ($VerifyTolerance) {
            Invoke-Check "official-task-tolerancing-real-completion" {
                if ($systemMode -match "NonSequential") { throw "Tolerancing Task completion requires a sequential fixture." }
                $taskId = Start-OfficialTask "zemax_run_tolerancing" @{
                    includeSensitivity = $true; criterion = "RMSSpotRadius";
                    criterionSampling = 3; criterionComp = "None"; criterionCycle = 1;
                    criterionField = "UserDefined"; monteCarloRuns = 3;
                    monteCarloStatistic = "Normal"; maxSensitivityOperands = 10;
                    timeoutSeconds = 120.0; runInBackground = $true
                }
                $actual = Assert-RealTaskCompletion $taskId "zemax_run_tolerancing"
                if ([int]$actual.monteCarloRows -lt 1) { throw "Task did not return real Monte Carlo output." }
                "task=$taskId; monteCarloRows=$($actual.monteCarloRows)"
            } | Out-Null
        }
        if ($VerifyTaskCancellation) {
            Invoke-Check "official-task-cancellation-and-grace" {
                if ($systemMode -match "NonSequential") { throw "Global-search cancellation requires a sequential fixture." }
                $taskId = Start-OfficialTask "zemax_global_search" @{
                    algorithm = "DLS"; cores = 0; solutionsToSave = 10;
                    timeoutSeconds = 0.0; runInBackground = $true
                }
                $initial = Get-OfficialTask $taskId
                if ($initial.status -ne "working") {
                    throw "The selected fixture is too short-lived for a meaningful live cancellation test."
                }
                $before = Get-Health
                $null = Invoke-Mcp -Method "tasks/cancel" -ToolName $taskId -Params @{ taskId = $taskId } -TaskOptIn
                $terminal = Wait-OfficialTask $taskId
                $after = Get-Health
                if ($terminal.status -eq "completed") {
                    throw "The Job completed before cancellation; use a harder optimization fixture."
                }
                if ($terminal.status -eq "failed" -and
                    [long]$before.worker.workerGeneration -eq [long]$after.worker.workerGeneration) {
                    throw "A failed cancelled Job did not demonstrate Worker hard recovery."
                }
                if ($terminal.status -notin @("cancelled", "failed")) {
                    throw "Cancellation did not reach an expected terminal state."
                }
                # A successful cancellation must release the Worker control slot.
                $alive = Get-ToolPayload (Invoke-Tool "zemax_status")
                "task=$taskId; terminal=$($terminal.status); generation=$($before.worker.workerGeneration)->$($after.worker.workerGeneration)"
            } | Out-Null
        }
    }

    Invoke-Check "save-temp-fixture" {
        if ("zemax_save_file" -notin $tools) { throw "Active profile does not expose zemax_save_file." }
        $saved = Get-ToolPayload (Invoke-Tool "zemax_save_file")
        if (-not (Test-Path -LiteralPath ([string]$saved.filePath))) { throw "SaveFile did not produce a file." }
        [string]$saved.filePath
    } | Out-Null

    if ($VerifyWorkerCrashRecovery) {
        Invoke-Check "worker-real-process-crash-recovery" {
            # Explicit destructive test on a disposable fixture ONLY. A process
            # kill verifies generation invalidation/restart, NOT the grace timer.
            $tool = if ($VerifyNsc) { "zemax_run_nsc_ray_trace" } else { "zemax_global_search" }
            $args = if ($VerifyNsc) {
                @{ clearDetectors = $true; detectorObject = 0; splitRays = $false;
                   scatterRays = $false; usePolarization = $false; ignoreErrors = $true;
                   timeoutSeconds = 120.0; runInBackground = $true }
            }
            else { @{ algorithm = "DLS"; cores = 0; solutionsToSave = 10;
                      timeoutSeconds = 0.0; runInBackground = $true } }
            $taskId = Start-OfficialTask $tool $args
            $pre = Get-Health
            $generation = [long]$pre.worker.workerGeneration
            $workerPid = [int]$pre.worker.workerPid
            if ($workerPid -le 0 -or $generation -le 0) { throw "Worker health did not expose a valid PID/generation." }
            if ($workerPid -eq $PID) { throw "Refusing to kill the current verifier process." }
            $workerProc = Get-CimInstance Win32_Process -Filter "ProcessId = $workerPid"
            if ($null -eq $workerProc -or
                [string]$workerProc.Name -notmatch '^ZemaxMCP\\.Worker\\.exe$') {
                throw "PID $workerPid is not the dedicated ZemaxMCP.Worker.exe. Refusing to terminate it."
            }
            Stop-Process -Id $workerPid -Force -ErrorAction Stop
            Start-Sleep -Seconds 2
            $recovered = Get-Health
            if ([long]$recovered.worker.workerGeneration -le $generation) {
                throw "Worker generation was not replaced after terminating the dedicated Worker process."
            }
            $terminal = Wait-OfficialTask $taskId
            if ($terminal.status -ne "failed") {
                throw "A Task from a terminated Worker generation should fail, got $($terminal.status)."
            }
            $alive = Get-ToolPayload (Invoke-Tool "zemax_status")
            "oldWorker=$workerPid; generation=$generation->$($recovered.worker.workerGeneration); oldTask=$($terminal.status)"
        } | Out-Null
    }
}

finally {
    $healthAfter = $null
    try { $healthAfter = Get-Health } catch { }
    $report = [ordered]@{
        generatedAt = [DateTimeOffset]::UtcNow
        endpoint = $endpointUri
        fixture = $fixture
        workingCopy = $workingCopy
        workingCopyRetained = ($KeepWorkingCopy -or $script:openAttempted)
        protocolVersion = $script:protocolVersion
        toolCount = $tools.Count
        toolset = if ($healthBefore) { $healthBefore.toolset } else { $null }
        hostVersion = if ($healthBefore) { $healthBefore.hostVersion } else { $null }
        workerVersion = if ($healthBefore) { $healthBefore.workerVersion } else { $null }
        zosApiAssemblyVersion = if ($healthBefore) { $healthBefore.zosApiAssemblyVersion } else { $null }
        zosApiFileVersion = if ($healthBefore) { $healthBefore.zosApiFileVersion } else { $null }
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

    # The active OpticStudio system may still point to this working copy.
    # Never delete an open/tested lens underneath an active session.
    if (-not $KeepWorkingCopy -and -not $script:openAttempted) {
        try { Remove-Item -LiteralPath $runRoot -Recurse -Force } catch { }
    }
    else {
        Write-Host "Working fixture retained for safety: $workingCopy"
        Write-Host "Close or switch the OpticStudio system before manually deleting this temporary directory."
    }
}

$failures = @($results | Where-Object { $_.status -eq "FAIL" })
if ($failures.Count -gt 0) { throw "Functional live verification failed in $($failures.Count) check(s). See $ReportPath." }
Write-Host "Functional live verification completed successfully."
