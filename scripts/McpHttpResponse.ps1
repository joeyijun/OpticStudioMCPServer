# Consumer-side acceptance helper, not an MCP transport implementation.
# A completed HTTP response can contain notifications/requests before its result.
function Wait-McpWorkerRecovery {
    param([scriptblock]$Probe, [long]$PreviousGeneration, [int]$TimeoutSeconds = 90, [int]$PollMilliseconds = 300)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = 'Worker has not reported a newer connected generation with a valid license.'
    do {
        $remaining = [int][Math]::Ceiling(($deadline - [DateTimeOffset]::UtcNow).TotalSeconds)
        if ($remaining -lt 1) { break }
        try {
            $health = & $Probe ([Math]::Min(30, $remaining))
            if ([long]$health.worker.workerGeneration -gt $PreviousGeneration -and
                $health.zosApiConnected -eq $true -and $health.licenseValidForApi -eq $true) { return $health }
        } catch { $lastError = $_.Exception.Message }
        if ($PollMilliseconds -gt 0) { Start-Sleep -Milliseconds $PollMilliseconds }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Worker recovery did not become ready within $TimeoutSeconds seconds: $lastError"
}

function ConvertFrom-McpToolResult {
    param($Response)
    $text = [string](@($Response.result.content | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text)
    if ([string]::IsNullOrWhiteSpace($text)) { throw 'Tool returned no text payload.' }
    $payload = $null
    $parseError = $null
    try { $payload = $text | ConvertFrom-Json -ErrorAction Stop } catch { $parseError = $_ }
    if ($Response.result.isError -eq $true) {
        $reason = if ($payload -and $payload.error) { [string]$payload.error } else { $text }
        throw "MCP isError=true: $reason"
    }
    if ($parseError) { throw "Tool returned a non-JSON success payload: $text" }
    if ($payload -and $payload.PSObject.Properties.Name -contains 'success' -and $payload.success -eq $false) {
        throw "Tool success=false: $($payload.error)"
    }
    return $payload
}

function ConvertFrom-McpWireResponse {
    param([Parameter(Mandatory=$true)]$Response, [Parameter(Mandatory=$true)]$RequestId)
    $content = [string]$Response.Content
    if ([string]::IsNullOrWhiteSpace($content)) { throw 'MCP response is empty.' }
    $messages = [Collections.Generic.List[string]]::new()
    if ([string]$Response.Headers['Content-Type'] -match 'text/event-stream' -or $content -match '(?m)^data:') {
        $data = [Collections.Generic.List[string]]::new()
        foreach ($line in @($content -split "\r\n|\n|\r") + @('')) {
            if ($line.Length -eq 0) {
                if ($data.Count -gt 0) { $messages.Add(($data -join "`n")); $data.Clear() }
            } elseif ($line.StartsWith('data:', [StringComparison]::Ordinal)) {
                $value = $line.Substring(5)
                if ($value.StartsWith(' ')) { $value = $value.Substring(1) }
                $data.Add($value)
            }
        }
    } else { $messages.Add($content) }
    $expected = ConvertTo-Json -InputObject $RequestId -Compress
    $matched = $null
    foreach ($message in $messages) {
        $rpc = $message | ConvertFrom-Json -ErrorAction Stop
        if ($rpc.jsonrpc -ne '2.0' -or $rpc.PSObject.Properties.Name -contains 'method') { continue }
        if ($rpc.PSObject.Properties.Name -notcontains 'id') { continue }
        if ((ConvertTo-Json -InputObject $rpc.id -Compress) -ne $expected) { continue }
        if ($rpc.PSObject.Properties.Name -notcontains 'result' -and $rpc.PSObject.Properties.Name -notcontains 'error') { continue }
        if ($null -ne $matched) { throw 'Duplicate MCP response ID in one HTTP response.' }
        $matched = $rpc
    }
    if ($null -eq $matched) { throw 'MCP response has no result/error matching the original request ID.' }
    return $matched
}
