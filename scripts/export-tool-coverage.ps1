[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/tool-coverage.json'),
    [string[]]$AcceptanceReportPaths = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$catalogPath = Join-Path $root 'src/ZemaxMCP.Toolsets/ToolsetCatalog.cs'
$source = Get-Content -LiteralPath $catalogPath -Raw
$matches = [regex]::Matches($source, '(?m)^\s*\["(zemax_[a-z0-9_]+)"\]\s*=\s*"([^"]+)"')
$inventory = @{}
foreach ($found in $matches) {
    $name = $found.Groups[1].Value
    if ($inventory.ContainsKey($name)) { throw "Duplicate public tool in explicit catalog: $name" }
    $inventory[$name] = $found.Groups[2].Value
}
if ($inventory.Count -ne 144) {
    throw "Explicit tool-domain inventory contains $($inventory.Count) tools, expected 144."
}

# An acceptance case proves a workflow scenario, not every possible call or
# every numerical edge case of one listed tool.
$scenarioTools = @{
    'sequential-footprint-and-energy-budget' = @('zemax_energy_budget','zemax_ray_footprint','zemax_aperture_throughput')
    'sequential-ray-diagnostics' = @('zemax_ray_trace_diagnostics')
    'sequential-analysis-cardinal-points' = @('zemax_cardinal_points')
    'nsc-structural-summary' = @('zemax_nsc_scene_summary','zemax_get_nsc_objects','zemax_run_nsc_ray_trace')
    'nsc-detector-pixels-energy-budget' = @('zemax_get_nsc_detector','zemax_nsc_energy_budget')
    'tolerance-structural-and-live-run' = @('zemax_tolerance_summary','zemax_get_tolerances','zemax_run_tolerancing')
    'official-task-nsc-real-completion' = @('zemax_run_nsc_ray_trace')
    'official-task-tolerancing-real-completion' = @('zemax_run_tolerancing')
    'official-task-cancellation-and-grace' = @('zemax_global_search','zemax_job_cancel','zemax_job_status')
    'worker-real-process-crash-recovery' = @('zemax_status')
    'background-job-lifecycle' = @('zemax_global_search','zemax_job_cancel','zemax_job_status')
    'local-optimization-one-cycle' = @('zemax_optimize')
    'sequential-edit-readback' = @('zemax_add_surface','zemax_set_surface','zemax_batch_set_surfaces','zemax_remove_surface','zemax_get_system')
}
$evidence = @{}
foreach ($path in $AcceptanceReportPaths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Acceptance report does not exist: $path"
    }
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if (-not $report.generatedAt -or -not $report.zosApiAssemblyVersion -or
        -not $report.fixture -or -not $report.tests) {
        throw "Invalid or incomplete licensed acceptance JSON: $path"
    }
    foreach ($test in $report.tests) {
        $name = [string]$test.name
        if ([string]$test.status -ne 'PASS' -or -not $scenarioTools.ContainsKey($name)) { continue }
        foreach ($tool in $scenarioTools[$name]) {
            if (-not $evidence.ContainsKey($tool)) {
                $evidence[$tool] = [Collections.Generic.List[object]]::new()
            }
            $evidence[$tool].Add([pscustomobject]@{
                scenario = $name
                report = [IO.Path]::GetFullPath($path)
                at = $report.generatedAt
                apiVersion = $report.zosApiAssemblyVersion
                fixture = $report.fixture
                evidenceLevel = 'licensed-workflow-scenario'
            })
        }
    }
}

$tools = @($inventory.Keys | Sort-Object | ForEach-Object {
    [pscustomobject]@{
        name = $_
        domain = $inventory[$_]
        contractDiscovered = $true
        licensedEvidenceLevel = $(if ($evidence.ContainsKey($_)) { 'scenario-observed' } else { 'not-provided' })
        scenarios = $(if ($evidence.ContainsKey($_)) { @($evidence[$_]) } else { @() })
    }
})
$summary = [ordered]@{
    generatedAt = [DateTimeOffset]::UtcNow
    disclaimer = 'Passing CI validates tool registration, not real ZOS-API measurements. Passing a scenario is not exhaustive tool validation.'
    totalTools = $tools.Count
    toolsWithLicensedScenarioEvidence = @($tools | Where-Object { $_.licensedEvidenceLevel -eq 'scenario-observed' }).Count
    tools = $tools
}
$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$summary | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Tool coverage inventory: $($tools.Count) public tools; $($summary.toolsWithLicensedScenarioEvidence) with provided scenario-level licensed evidence. Report: $OutputPath"
