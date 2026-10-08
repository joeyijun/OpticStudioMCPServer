$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'McpHttpResponse.ps1')
function Response([string]$text, [string]$type='text/event-stream') { return @{Content=$text;Headers=@{'Content-Type'=$type}} }
$notification='{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":1}}'
$request='{"jsonrpc":"2.0","id":7,"method":"sampling/createMessage","params":{}}'
$result='{"jsonrpc":"2.0","id":7,"result":{"ok":true}}'
$wire=": heartbeat`r`nevent: message`r`ndata: $notification`r`n`r`ndata: $request`r`n`r`ndata: $result`r`n`r`n"
if (-not (ConvertFrom-McpWireResponse (Response $wire) 7).result.ok) { throw 'Progress/server request displaced final result.' }
$multi="data: {`ndata: `"jsonrpc`":`"2.0`",`ndata: `"id`":7,`ndata: `"result`":null}`n`n"
if ((ConvertFrom-McpWireResponse (Response $multi) 7).id -ne 7) { throw 'Multiline SSE data failed.' }
if (-not (ConvertFrom-McpWireResponse (Response $result 'application/json') 7).result.ok) { throw 'Plain JSON failed.' }
$errorFixture='{"jsonrpc":"2.0","id":"7","error":{"code":-32003,"message":"timeout"}}'
if ((ConvertFrom-McpWireResponse (Response "data: $errorFixture`n`n") '7').error.code -ne -32003) { throw 'Error ID lost.' }
foreach ($invalid in @("data: $result`n`ndata: $result`n`n", "data: $notification`n`n", "data: $errorFixture`n`n", 'data: {broken')) {
    $rejected=$false
    try { ConvertFrom-McpWireResponse (Response $invalid) 7 | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw 'Duplicate, missing, type-mismatched or malformed response ID accepted.' }
}
foreach ($fixture in @(
    @{ isError=$true; text='An error occurred invoking zemax_set_surface.'; expected='MCP isError=true: An error occurred' },
    @{ isError=$true; text='{"success":false,"error":"non-finite radius"}'; expected='MCP isError=true: non-finite radius' },
    @{ isError=$false; text='{"success":false,"error":"readback mismatch"}'; expected='Tool success=false: readback mismatch' },
    @{ isError=$false; text='not JSON'; expected='Tool returned a non-JSON success payload' }
)) {
    $toolResponse=@{result=@{isError=$fixture.isError;content=@(@{type='text';text=$fixture.text})}}
    $rejected=$false
    try { ConvertFrom-McpToolResult $toolResponse | Out-Null }
    catch { if ($_.Exception.Message -like ($fixture.expected+'*')) {$rejected=$true} else {throw} }
    if (-not $rejected) { throw 'Tool error payload classification regressed.' }
}
$successResponse=@{result=@{isError=$false;content=@(@{type='text';text='{"success":true,"radius":0}'})}}
if ((ConvertFrom-McpToolResult $successResponse).radius -ne 0) { throw 'Successful tool payload changed.' }
Write-Output 'MCP verifier JSON/SSE IDs, multiline data, rejection and plain-text/structured tool error tests passed.'
