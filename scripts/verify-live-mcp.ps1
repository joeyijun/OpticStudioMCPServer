[CmdletBinding()]
param(
    [string]$Endpoint = "http://127.0.0.1:8000/mcp",
    [int]$MinimumToolCount = 122,
    [switch]$Baseline118,
    [switch]$SkipReadOnlyCalls
)

$ErrorActionPreference = "Stop"
$endpointUri = $Endpoint.TrimEnd("/")
$sessionId = $null
$nextId = 1

function Invoke-McpRequest {
    param([string]$Method, [hashtable]$Params = @{})

    $requestId = $script:nextId++
    $payload = @{
        jsonrpc = "2.0"
        id = $requestId
        method = $Method
        params = $Params
    } | ConvertTo-Json -Depth 30 -Compress
    $headers = @{ Accept = "application/json, text/event-stream" }
    if ($script:sessionId) { $headers["Mcp-Session-Id"] = $script:sessionId }
    $response = Invoke-WebRequest -UseBasicParsing -Uri $script:endpointUri -Method Post `
        -ContentType "application/json" -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 60
    if (-not $script:sessionId -and $response.Headers["Mcp-Session-Id"]) {
        $script:sessionId = [string]$response.Headers["Mcp-Session-Id"]
    }
    $json = $response.Content | ConvertFrom-Json
    if ($json.error) { throw "$Method returned JSON-RPC error $($json.error.code): $($json.error.message)" }
    return $json
}

$health = Invoke-RestMethod -Uri ($endpointUri + "/health") -TimeoutSec 15
if (-not $health.bridgeRunning -or -not $health.mcpServerRunning) {
    throw "Bridge or MCP server is not running at $endpointUri."
}

$null = Invoke-McpRequest -Method "initialize" -Params @{
    protocolVersion = "2024-11-05"
    capabilities = @{}
    clientInfo = @{ name = "zemax-mcp-release-verifier"; version = "1.0" }
}
$list = Invoke-McpRequest -Method "tools/list"
$tools = @($list.result.tools)
$names = @($tools | ForEach-Object { $_.name })

$required = @(
    "zemax_get_system_metadata", "zemax_set_system_metadata",
    "zemax_get_environment", "zemax_set_environment",
    "zemax_get_polarization", "zemax_set_polarization", "zemax_get_units",
    "zemax_get_stop_surface", "zemax_set_stop_surface", "zemax_get_first_order_data",
    "zemax_get_vignetting", "zemax_set_vignetting", "zemax_clear_vignetting",
    "zemax_get_field_settings", "zemax_get_wavelength_settings",
    "zemax_get_system_files", "zemax_get_aperture_settings",
    "zemax_quick_focus", "zemax_scale_lens"
)
if (-not $Baseline118) {
    $required += "zemax_get_advanced_system_settings", "zemax_get_ray_aiming_settings",
        "zemax_get_material_catalog_settings", "zemax_get_nonsequential_system_settings"
}
$missing = @($required | Where-Object { $_ -notin $names })
if ($tools.Count -lt $MinimumToolCount) {
    throw "Expected at least $MinimumToolCount tools, but tools/list returned $($tools.Count)."
}
if ($missing.Count -gt 0) { throw "Required tools missing from tools/list: $($missing -join ', ')" }

Write-Host "Health OK: ZOS-API loaded=$($health.zosApiLoaded), connected=$($health.zosApiConnected), license=$($health.licenseStatus)"
Write-Host "tools/list OK: $($tools.Count) tools; all $($required.Count) release-candidate tools are present."

if (-not $SkipReadOnlyCalls) {
    $readOnlyTools = @(
        "zemax_status", "zemax_get_configuration", "zemax_get_system_metadata", "zemax_get_environment",
        "zemax_get_polarization", "zemax_get_units", "zemax_get_stop_surface", "zemax_get_first_order_data",
        "zemax_get_vignetting", "zemax_get_field_settings", "zemax_get_wavelength_settings",
        "zemax_get_system_files", "zemax_get_aperture_settings"
    )
    if (-not $Baseline118) {
        $readOnlyTools += "zemax_get_advanced_system_settings", "zemax_get_ray_aiming_settings",
            "zemax_get_material_catalog_settings"
    }
    foreach ($toolName in $readOnlyTools) {
        $result = Invoke-McpRequest -Method "tools/call" -Params @{ name = $toolName; arguments = @{} }
        if ($result.result.isError -eq $true) { throw "$toolName returned an MCP tool error." }
        $text = @($result.result.content | Where-Object { $_.type -eq "text" } | Select-Object -First 1).text
        if ($text) {
            $toolPayload = $null
            try { $toolPayload = $text | ConvertFrom-Json }
            catch { Write-Verbose "$toolName returned non-JSON text content; MCP transport result remains valid." }
            if ($toolPayload -and $toolPayload.PSObject.Properties.Name -contains "success" -and $toolPayload.success -eq $false) {
                throw "$toolName returned success=false: $($toolPayload.error)"
            }
        }
        Write-Host "Read-only call OK: $toolName"
    }
}

Write-Host "Live MCP verification completed successfully for $endpointUri."
