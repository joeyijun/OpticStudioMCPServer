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

## Job ownership and diagnostics

In scoped mode, every newly created background Job is registered against its authenticated credential ID and the active Worker generation. `zemax_job_status` and `zemax_job_cancel` reject IDs not owned by the caller; `zemax_job_list` returns only the caller's records and strips foreign result payloads. Task ownership continues to apply to completed Job history (subject to bounded retention), and a Worker-generation replacement invalidates old Job handles.

The older process-global `zemax_multistart_status` / `zemax_multistart_stop` APIs are hidden and blocked in scoped mode because they lack an owner/Job ID; use `zemax_job_status` / `zemax_job_cancel` instead. In scoped deployments `/mcp/health` and `/mcp/activity` return only a redacted heartbeat and the calling credential's role, never a global Job/operation list. Full legacy diagnostic responses remain available in single-user and shared-token modes.

These checks are implemented in the Host before invoking Worker RPC for direct Job status/cancel and after a Worker list response for result filtering. A non-owner cannot force Job cancellation by guessing an ID. A revoked client's already running COM task is **not** automatically terminated; it remains subject to its normal cancellation/timeout/recovery lifecycle.

**Scope:** This provides per-client authentication, strict mutation authorization, lease ownership, and revocation. It does **not** yet provide confidential per-client partitions of historical Job results, health diagnostics, or optical files, and revocation does not abort a running ZOS-API operation. Do not expose one Host to mutually untrusted tenants; use separate OpticStudio processes / Hosts for that level of isolation.


## HTTPS/TLS (draft branch, explicit opt-in)

Bearer authentication is **not transport encryption**. LAN HTTP exposes tokens and optical
data to anyone capable of observing the network path. Legacy local loopback still defaults to
HTTP; explicitly trusted isolated LAN HTTP remains supported with a visible warning.

To enable the Host's **single HTTPS listener** on a Zemax computer, provision a **trusted**
PKCS#12 (.pfx) server certificate with a private key and SAN containing the actual client
DNS name or IP address, and set the password **in the Host process environment**:

```powershell
$env:ZEMAX_MCP_TLS_PFX_PASSWORD = '<password from your secure deployment secret store>'
# Existing authentication, --allowed-host and --allowed-origin arguments remain required.
.\ZemaxMCP.Host.exe --host 0.0.0.0 --port 8000 --tls-pfx 'C:\secure\mcp-server.pfx' --allowed-host optics.example.org --allowed-origin https://optics.example.org:*
```

Use `--tls-password-env ENV_NAME` to select another existing password environment variable.
Never pass the password in CLI arguments, client preferences, logs, source code or public
README examples. Do not commit real certificates, private keys or tokens.

Then configure each AI client with `https://optics.example.org:8000/mcp` and the original
Bearer credential. The client must **validate** the TLS certificate chain and endpoint name.
Do not disable certificate verification to get a connection working. A self-signed certificate
must be deployed into an appropriate trusted store first; prefer a certificate issued by an
authorized organization CA.

This feature does not automatically generate, enroll, renew, rotate or trust certificates.
The current Launcher still starts its own local service in legacy HTTP mode; TLS is configured
on the Host process on the Zemax computer. PFX deployment, Windows ACLs, reverse proxies
(if used), endpoint connectivity and certificate renewal need operational acceptance.

### Launcher TLS diagnostics behind HTTPS reverse proxies

The Launcher reports the security of the **actual client endpoint URL**,
not just a downstream Host `tlsEnabled` flag. A remote HTTPS URL remains
HTTPS/TLS to the gateway when a trusted reverse proxy terminates TLS
before forwarding to the Host. A remote HTTP URL remains **unencrypted**
even if the Host independently reports that its Kestrel TLS option is
enabled. Loopback HTTP is explicitly identified as unencrypted local
transport. This status is not a certificate-chain validation or
a guarantee about the reverse proxy → Host hop; configure and audit
both independently.
