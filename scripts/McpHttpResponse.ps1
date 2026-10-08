# Consumer-side acceptance helper, not an MCP transport implementation.
# A completed HTTP response can contain notifications/requests before its result.
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
