# Per-client MCP authentication (opt-in)

The original local default remains unchanged: loopback-only binding can run without a token. The existing `ZEMAX_MCP_TOKEN` shared-bearer mode also remains available. These two legacy modes do not offer per-client revocation or separate read/write privileges.

For multiple trusted users or AI applications, choose **scoped credentials** instead. Each client receives a different bearer token and a stable server-owned identity. The Host uses that identity for the OpticStudio control lease, regardless of any client-supplied name, version, or instance header.

## Generate credentials

Use PowerShell 7 or later on the **Host** computer. Generate high-entropy tokens locally:

```powershell
function New-McpClient {
    param([string]$Id, [ValidateSet('read-only', 'read-write')][string]$Permission)
    $secret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
    $hash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($secret))
    ).ToLowerInvariant()
    [pscustomobject]@{ Id = $Id; Permission = $Permission; Secret = $secret; TokenSha256 = $hash }
}
$reader = New-McpClient 'inspector' 'read-only'
$editor = New-McpClient 'designer' 'read-write'

$path = Join-Path $env:LOCALAPPDATA 'ZemaxMCP\clients.json'
New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
@{
    version = 1
    clients = @(
        @{ id = $reader.Id; permission = $reader.Permission; tokenSha256 = $reader.TokenSha256 }
        @{ id = $editor.Id; permission = $editor.Permission; tokenSha256 = $editor.TokenSha256 }
    )
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8

# Show each raw token only to its intended client through a private channel:
$reader.Secret
$editor.Secret
```

The file stores only SHA-256 digests, not the raw bearer secrets. Use a unique, unpredictable 32-byte token per client, protect the file with local filesystem permissions, and do not commit it to the repository. The Host accepts between 1 and 64 uniquely identified clients.

Start the Host using `--client-credentials-file <absolute-path-to-clients.json>` or set `ZEMAX_MCP_CLIENTS_FILE` to that path. **Unset `ZEMAX_MCP_TOKEN`** in this mode; mixing the shared credential and per-client credentials is explicitly rejected at startup. LAN binding still requires a concrete allowed Host and appropriate allowed Origins. Use TLS via a trusted reverse proxy for network deployments: ordinary HTTP exposes bearer tokens to network observers.

Clients pass their own raw token as an `Authorization: Bearer <token>` header. The `read-only` permission exposes **only tools with an actual ReadOnly impact**, excluding even Caution operations such as opening a model, cancellation, and detector ray tracing. The `read-write` permission uses the configured Host toolset; global `--read-only true` and profile restrictions remain effective for all clients.

## Revocation and rotation

To revoke a client, remove its entry from the JSON file. To rotate, replace the client's `tokenSha256` with the digest of its new token. Write the complete file with an atomic file replacement where practical. The Host reads and validates the credential file **for every request**: revocation takes effect at the next request with no restart. A missing or malformed live file returns HTTP 503 for protected routes and does not fall back to unauthenticated or shared-token access.

A token identifies the client, not the arbitrary `clientInfo` name it sends. Different tokens cannot impersonate each other's control lease by spoofing the same name or instance ID.

**Scope:** This provides per-client authentication, strict mutation authorization, lease ownership, and revocation. It does **not** yet provide confidential per-client partitions of historical Job results, health diagnostics, or optical files, and revocation does not abort a running ZOS-API operation. Do not expose one Host to mutually untrusted tenants; use separate OpticStudio processes / Hosts for that level of isolation.
