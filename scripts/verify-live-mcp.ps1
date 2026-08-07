[CmdletBinding()]
param(
    [string]$Endpoint = "http://127.0.0.1:8000/mcp",
    [string]$AccessToken = $env:ZEMAX_MCP_TOKEN,
    [int]$ExpectedFullExpertToolCount = 126,
    [switch]$SkipReadOnlyCalls,
    [switch]$VerifySafety,
    [switch]$VerifyLegacyCompatibility
)

$ErrorActionPreference = "Stop"
$endpointUri = $Endpoint.TrimEnd("/")
$nextId = 1
$clientName = "zemax-mcp-release-verifier"
$clientVersion = "2.0"
$clientInstanceId = "release-" + [Guid]::NewGuid().ToString("N")
$modernProtocolVersion = "2026-07-28"
$legacyProtocolVersion = "2025-11-25"

function ConvertFrom-McpHttpResponse {
    param($Response)

    $content = [string]$Response.Content
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    $contentType = [string]$Response.Headers["Content-Type"]
    if ($contentType -match "text/event-stream" -or $content -match "(?m)^data:") {
        $dataLine = @($content -split "`r?`n" | Where-Object { $_ -match '^data:' } | Select-Object -First 1)
        if ($dataLine.Count -eq 0) { throw "MCP SSE response contained no data payload." }
        $content = ([string]$dataLine[0]).Substring(5).Trim()
    }
    return $content | ConvertFrom-Json
}

function New-ModernMeta {
    return @{
        "io.modelcontextprotocol/protocolVersion" = $script:modernProtocolVersion
        "io.modelcontextprotocol/clientInfo" = @{
            name = $script:clientName
            version = $script:clientVersion
        }
        "io.modelcontextprotocol/clientCapabilities" = @{}
        "io.zemaxmcp/clientInstanceId" = $script:clientInstanceId
    }
}

function Invoke-ModernMcpRequest {
    param(
        [string]$Method,
        [hashtable]$Params = @{},
        [string]$ToolName
    )

    $requestId = $script:nextId++
    $wireParams = @{}
    foreach ($key in $Params.Keys) { $wireParams[$key] = $Params[$key] }
    $wireParams["_meta"] = New-ModernMeta
    $payload = @{
        jsonrpc = "2.0"
        id = $requestId
        method = $Method
        params = $wireParams
    } | ConvertTo-Json -Depth 50 -Compress

    $headers = @{
        Accept = "application/json, text/event-stream"
        "MCP-Protocol-Version" = $script:modernProtocolVersion
        "Mcp-Method" = $Method
        "X-Zemax-MCP-Client-Instance" = $script:clientInstanceId
    }
    if (-not [string]::IsNullOrWhiteSpace($ToolName)) { $headers["Mcp-Name"] = $ToolName }
    if (-not [string]::IsNullOrWhiteSpace($script:AccessToken)) { $headers["Authorization"] = "Bearer $script:AccessToken" }

    $response = Invoke-WebRequest -UseBasicParsing -Uri $script:endpointUri -Method Post `
        -ContentType "application/json" -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 90
    $json = ConvertFrom-McpHttpResponse $response
    if ($json -and $json.error) { throw "$Method returned JSON-RPC error $($json.error.code): $($json.error.message)" }
    return $json
}

function Invoke-ModernToolCall {
    param([string]$Name, [hashtable]$Arguments = @{})
    return Invoke-ModernMcpRequest -Method "tools/call" -ToolName $Name -Params @{ name = $Name; arguments = $Arguments }
}

function Invoke-LegacyInitialize {
    $requestId = $script:nextId++
    $payload = @{
        jsonrpc = "2.0"
        id = $requestId
        method = "initialize"
        params = @{
            protocolVersion = $script:legacyProtocolVersion
            capabilities = @{}
            clientInfo = @{ name = "$($script:clientName)-legacy"; version = $script:clientVersion }
        }
    } | ConvertTo-Json -Depth 20 -Compress
    $headers = @{ Accept = "application/json, text/event-stream" }
    if (-not [string]::IsNullOrWhiteSpace($script:AccessToken)) { $headers["Authorization"] = "Bearer $script:AccessToken" }
    $response = Invoke-WebRequest -UseBasicParsing -Uri $script:endpointUri -Method Post `
        -ContentType "application/json" -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 30
    $json = ConvertFrom-McpHttpResponse $response
    if ($json.error) { throw "Legacy initialize returned JSON-RPC error $($json.error.code): $($json.error.message)" }
    if ($json.result.protocolVersion -ne $script:legacyProtocolVersion) {
        throw "Legacy initialize negotiated '$($json.result.protocolVersion)' instead of '$($script:legacyProtocolVersion)'."
    }
    return $json
}

function Get-McpHealth {
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($script:AccessToken)) { $headers["Authorization"] = "Bearer $script:AccessToken" }
    return Invoke-RestMethod -Uri ($script:endpointUri + "/health") -Headers $headers -TimeoutSec 30
}

function Get-ToolText {
    param($Response)
    return [string](@($Response.result.content | Where-Object { $_.type -eq "text" } | Select-Object -First 1).text)
}

function Get-ToolPayload {
    param($Response)
    $text = Get-ToolText $Response
    if ([string]::IsNullOrWhiteSpace($text)) { throw "The MCP tool response contained no text payload." }
    try { return $text | ConvertFrom-Json }
    catch { throw "The MCP tool response was not JSON: $text" }
}

function Assert-PublicToolContract {
    param($Tools)

    if (@($Tools).Count -eq 0) { throw "tools/list returned no tools." }
    $duplicates = @($Tools | Group-Object name | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    if ($duplicates.Count -gt 0) { throw "tools/list returned duplicate tool names: $($duplicates -join ', ')" }

    foreach ($tool in $Tools) {
        if ([string]::IsNullOrWhiteSpace([string]$tool.name)) { throw "tools/list returned a tool without a name." }
        if ([string]::IsNullOrWhiteSpace([string]$tool.description)) { throw "$($tool.name) has no public description." }
        if (-not $tool.inputSchema -or $tool.inputSchema.type -ne "object") { throw "$($tool.name) does not expose an object input schema." }
    }

    $openFile = @($Tools | Where-Object { $_.name -eq "zemax_open_file" } | Select-Object -First 1)
    if ($openFile.Count -gt 0 -and "filePath" -notin @($openFile[0].inputSchema.required)) {
        throw "zemax_open_file.filePath is no longer required in the public schema."
    }

    $setSurface = @($Tools | Where-Object { $_.name -eq "zemax_set_surface" } | Select-Object -First 1)
    if ($setSurface.Count -gt 0) {
        $required = @($setSurface[0].inputSchema.required)
        if ("surfaceNumber" -notin $required) { throw "zemax_set_surface.surfaceNumber must remain required." }
        foreach ($optionalName in @("material", "comment", "isStop", "radiusVariable", "thicknessVariable", "conicVariable")) {
            if ($optionalName -in $required) { throw "zemax_set_surface.$optionalName must remain optional so omission means leave unchanged." }
        }
    }
}

# First prove that the public 2026 stateless discovery path works. This request
# is intentionally sent before /health because health is Worker-backed and may
# lazily start OpticStudio.
$list = Invoke-ModernMcpRequest -Method "tools/list"
$tools = @($list.result.tools)
Assert-PublicToolContract $tools
$names = @($tools | ForEach-Object { $_.name })
foreach ($mustHave in @("zemax_status", "zemax_get_system", "zemax_tool_catalog")) {
    if ($mustHave -notin $names) { throw "Required core tool missing from tools/list: $mustHave" }
}
Write-Host "Modern MCP 2026-07-28 tools/list OK: $($tools.Count) policy-visible tools."

# Health intentionally starts the Worker and validates the authenticated private
# RPC contract used by all subsequent live tool calls.
$health = Get-McpHealth
if (-not $health.bridgeRunning -or -not $health.mcpServerRunning) {
    throw "Host or Worker is not running at $endpointUri."
}
if ($health.rpcVersion -ne $health.workerRpcVersion) {
    throw "Host/Worker private RPC version mismatch: host=$($health.rpcVersion), worker=$($health.workerRpcVersion)."
}
if ([string]::IsNullOrWhiteSpace([string]$health.manifestFingerprint) -or
    $health.manifestFingerprint -ne $health.workerManifestFingerprint) {
    throw "Host/Worker static tool-contract fingerprint mismatch."
}
if ($health.toolset -eq "full-expert" -and -not $health.readOnly -and $tools.Count -ne $ExpectedFullExpertToolCount) {
    throw "full-expert read/write tools/list returned $($tools.Count) tools; expected exactly $ExpectedFullExpertToolCount."
}
Write-Host "Health OK: RPC=$($health.rpcVersion), toolset=$($health.toolset), ZOS-API loaded=$($health.zosApiLoaded), connected=$($health.zosApiConnected), license=$($health.licenseStatus), read-only=$($health.readOnly)."

$catalogResponse = Invoke-ModernToolCall -Name "zemax_tool_catalog"
if ($catalogResponse.result.isError -eq $true) { throw "zemax_tool_catalog returned an MCP tool error: $(Get-ToolText $catalogResponse)" }
$catalog = Get-ToolPayload $catalogResponse
if ([int]$catalog.totalTools -ne $tools.Count) {
    throw "zemax_tool_catalog reports $($catalog.totalTools) policy-visible tools but tools/list returned $($tools.Count)."
}
Write-Host "Static manifest/Worker catalogue agreement OK: $($catalog.totalTools) tools across $(@($catalog.groups).Count) active groups."

if (-not $SkipReadOnlyCalls) {
    $readOnlyTools = @(
        "zemax_status", "zemax_get_system", "zemax_get_configuration", "zemax_get_system_metadata", "zemax_get_environment",
        "zemax_get_polarization", "zemax_get_units", "zemax_get_stop_surface", "zemax_get_first_order_data",
        "zemax_get_vignetting", "zemax_get_field_settings", "zemax_get_wavelength_settings",
        "zemax_get_system_files", "zemax_get_aperture_settings", "zemax_get_advanced_system_settings",
        "zemax_get_ray_aiming_settings", "zemax_get_material_catalog_settings"
    )
    foreach ($toolName in $readOnlyTools) {
        if ($toolName -notin $names) {
            Write-Verbose "Skipping $toolName because the active toolset does not expose it."
            continue
        }
        $result = Invoke-ModernToolCall -Name $toolName
        if ($result.result.isError -eq $true) { throw "$toolName returned an MCP tool error: $(Get-ToolText $result)" }
        $text = Get-ToolText $result
        if ($text) {
            $toolPayload = $null
            $parsedJson = $true
            try { $toolPayload = $text | ConvertFrom-Json }
            catch { $parsedJson = $false }
            if ($parsedJson -and $toolPayload.PSObject.Properties.Name -contains "success" -and $toolPayload.success -eq $false) {
                throw "$toolName returned success=false: $($toolPayload.error)"
            }
            if (-not $parsedJson) { Write-Verbose "$toolName returned non-JSON text content; MCP transport result remains valid." }
        }
        Write-Host "Read-only call OK: $toolName"
    }
}

if ($VerifySafety) {
    if ("zemax_get_system_metadata" -notin $names) { throw "The active toolset does not expose zemax_get_system_metadata; cannot verify mutation safety." }
    $beforeHealth = Get-McpHealth
    $metadata = Get-ToolPayload (Invoke-ModernToolCall -Name "zemax_get_system_metadata")
    if ($metadata.success -ne $true) { throw "Could not read system metadata for safety verification: $($metadata.error)" }
    $sameMetadata = @{ title = [string]$metadata.title; author = [string]$metadata.author; notes = [string]$metadata.notes }

    $writeAttempt = Invoke-ModernToolCall -Name "zemax_set_system_metadata" -Arguments $sameMetadata
    $afterHealth = Get-McpHealth
    if ($beforeHealth.readOnly) {
        $text = Get-ToolText $writeAttempt
        if ($writeAttempt.result.isError -ne $true -or $text -notmatch "read-only|does not permit") {
            throw "Read-only mode did not block a live mutating tool call before Worker execution."
        }
        if ($afterHealth.lastSnapshotPath -ne $beforeHealth.lastSnapshotPath) { throw "A blocked operation unexpectedly created a snapshot." }
        Write-Host "Live safety OK: read-only policy blocked a metadata write before ZOS-API mutation."
    }
    else {
        $payload = Get-ToolPayload $writeAttempt
        if ($writeAttempt.result.isError -eq $true -or $payload.success -ne $true) { throw "The no-op metadata write failed: $($payload.error)" }
        if ([string]::IsNullOrWhiteSpace([string]$afterHealth.lastSnapshotPath) -or
            $afterHealth.lastSnapshotPath -eq $beforeHealth.lastSnapshotPath -or
            $afterHealth.lastSnapshotPath -notlike "*.zos") {
            throw "The live mutating call did not report a new verified .zos safety snapshot."
        }
        Write-Host "Live safety OK: a no-op metadata write created $($afterHealth.lastSnapshotPath) before execution."
    }
}

if ($VerifyLegacyCompatibility) {
    $legacy = Invoke-LegacyInitialize
    Write-Host "Legacy MCP compatibility OK: initialize negotiated $($legacy.result.protocolVersion)."
}

Write-Host "Live MCP release verification completed successfully for $endpointUri."
