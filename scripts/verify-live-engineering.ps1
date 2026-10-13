param([switch]$VerifyPreview,
    [string]$SettingsPath = "$env:LOCALAPPDATA\ZemaxMCP\launcher-settings.json",
    [string]$ReportPath = (Join-Path $PSScriptRoot '../artifacts/live-engineering.json'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'McpHttpResponse.ps1')
$preferences = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
$endpoint = $preferences.remoteEndpoint.TrimEnd('/')
if (!$endpoint.StartsWith('https://')) { throw 'An explicitly configured HTTPS remote endpoint is required.' }
$token = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
    [Convert]::FromBase64String($preferences.remoteTokenProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
$instance = 'engineering-readonly-' + [Guid]::NewGuid().ToString('N')
$requestId = 0
$results = New-Object Collections.Generic.List[object]
function Request([string]$Method, [hashtable]$Parameters, [string]$ToolName) {
    $script:requestId++
    $Parameters['_meta'] = @{
        'io.modelcontextprotocol/protocolVersion' = '2026-07-28'
        'io.modelcontextprotocol/clientInfo' = @{ name='zemax-engineering-readonly-verifier';version='1.0' }
        'io.modelcontextprotocol/clientCapabilities' = @{}
        'io.zemaxmcp/clientInstanceId' = $instance
    }
    $headers = @{ Authorization='Bearer '+$token;Accept='application/json, text/event-stream';
        'MCP-Protocol-Version'='2026-07-28';'Mcp-Method'=$Method;'X-Zemax-MCP-Client-Instance'=$instance }
    if($ToolName) { $headers['Mcp-Name']=$ToolName }
    $payload = @{jsonrpc='2.0';id=$script:requestId;method=$Method;params=$Parameters} | ConvertTo-Json -Depth 40 -Compress
    $response = Invoke-WebRequest -UseBasicParsing -Uri $endpoint -Method Post -ContentType 'application/json' -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 90
    $rpc = ConvertFrom-McpWireResponse -Response $response -RequestId $script:requestId
    if($rpc.error) { throw ($rpc.error | ConvertTo-Json -Compress) }
    return $rpc.result
}
function Tool([string]$Name,[hashtable]$Arguments) {
    $result = Request 'tools/call' @{name=$Name;arguments=$Arguments} $Name
    if($result.isError) { throw (($result.content | Where-Object type -eq 'text').text -join '') }
    return $result
}
function Body($Result) {
    return (($Result.content | Where-Object type -eq 'text').text -join '') | ConvertFrom-Json
}
function Assert([bool]$Passed,[string]$Name) {
    $results.Add(@{name=$Name;passed=$Passed})
    if(!$Passed) { throw "Read-only acceptance failed: $Name" }
    Write-Output "[PASS] $Name"
}
$before = Invoke-RestMethod -Uri ($endpoint+'/health') -Headers @{Authorization='Bearer '+$token} -TimeoutSec 30
if($before.workerBusy) { throw 'Worker busy; no optical checks were started.' }
try {
    $listing = Request 'tools/list' @{} ''
    foreach($name in @('zemax_workflow_baseline','zemax_workflow_verify','zemax_compare_analyses')) {
        Assert (@($listing.tools.name) -contains $name) "discovery-$name"
    }
    $baseline = Body (Tool 'zemax_workflow_baseline' @{surfaceNumbers=@(0)})
    Assert ($baseline.success -and $baseline.baseline.id.Length -eq 32) 'baseline-readonly'
    $verified = Body (Tool 'zemax_workflow_verify' @{baselineId=$baseline.baseline.id})
    Assert ($verified.success -and !$verified.editRecorded -and !$verified.readbackVerified) 'no-edit-is-not-verified-edit'
    if($VerifyPreview) {
        Assert (@($listing.tools.name) -contains 'zemax_preview_surface_edits') 'discovery-dry-run-preview'
        $preview = Body (Tool 'zemax_preview_surface_edits' @{
            baselineId=$baseline.baseline.id
            edits=@(@{surfaceNumber=0;comment='Read-only preview; this value must not be written.'})
        })
        Assert ($preview.success -and $preview.dryRun -and $preview.requiresApproval) 'preview-is-not-an-edit-or-approval'
        $stillUnedited = Body (Tool 'zemax_workflow_verify' @{baselineId=$baseline.baseline.id})
        Assert ($stillUnedited.workflowState -eq 'awaiting_edit' -and !$stillUnedited.saveAuthorized) 'preview-does-not-consume-baseline'
    }
    $first = Tool 'zemax_spot_diagram' @{field=1;wavelength=0;rings=3}
    $second = Tool 'zemax_spot_diagram' @{field=1;wavelength=0;rings=3}
    Assert ($first.structuredContent.engineering.analysisId.Length -eq 32 -and
        $first.structuredContent.engineering.operations[0].status -eq 'known') 'spot-provenance'
    $compare = Body (Tool 'zemax_compare_analyses' @{
        baselineAnalysisId=$first.structuredContent.engineering.analysisId
        currentAnalysisId=$second.structuredContent.engineering.analysisId
        rules=@(@{path='/rmsSpotRadius';goal='range';minimum=0})
    })
    Assert ($compare.success -and $compare.criteriaPassed) 'spot-same-conditions-comparison'
    $finalBaseline = Body (Tool 'zemax_workflow_baseline' @{surfaceNumbers=@(0)})
    Assert ($finalBaseline.baseline.stateHash -eq $baseline.baseline.stateHash) 'guarded-model-state-unchanged'
    $after = Invoke-RestMethod -Uri ($endpoint+'/health') -Headers @{Authorization='Bearer '+$token} -TimeoutSec 30
    Assert ($after.lastSnapshotPath -eq $before.lastSnapshotPath) 'no-snapshot-created'
} finally {
    $report = @{sourceCommit=$before.workerSourceCommit;workerVersion=$before.workerInformationalVersion;
        checks=@($results.ToArray());scope='Read-only baseline, unchanged-lens analysis and comparison only. Guarded writes and full design acceptance were not exercised.'}
    $directory = Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent
    [void][IO.Directory]::CreateDirectory($directory)
    $report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
