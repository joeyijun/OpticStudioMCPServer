- **Static Host tool contract** — a build-time Roslyn generator produces the 126 tool names, descriptions, JSON schemas, domains, and impact levels. `tools/list` is answered by the Host without starting OpticStudio or the Worker.
- **Hardened Host / Worker isolation** — MCP ends at the Host. The `net48` Worker accepts only private RPC v3, keeps STA/ZOS-API state, and never exposes a network transport. The Host verifies Worker PID, per-launch secret, RPC version, and the static manifest SHA-256 fingerprint before ZOS-API initialization or any OpticStudio COM operation.
- **Transport-independent control lease** — modern MCP requests can be stateless while OpticStudio remains deliberately single-owner, single-STA, and serialized. Per-instance identity prevents supported same-machine clients from collapsing into one owner.
- **Authenticated LAN use** — every launcher-managed request uses a random Bearer token. LAN listening is refused without a token, and token rotation is one click.
- **Lens-change safety** — one explicit metadata catalogue drives both the MCP risk display and ZOS-API protection. Read-only mode blocks high-impact operations; in read/write mode, every recognised mutation first saves a timestamped `.zmx` copy of the current lens. ZMX is deliberately used as the cross-version safety format because `.ZOS` was not introduced until OpticStudio 21.3; unknown execution commands fail closed as high impact.
- **Structured progress/events** — Worker job and snapshot callbacks use a serialized event queue. The Host dispatches them independently from result processing, retains recent state for diagnostics, and forwards matching operation progress through MCP when a request supplied a progress token.
- **Verified, clean updates** — release metadata is RSA-signed, the ZIP size and SHA-256 are checked before extraction, and the updater replaces superseded program files while retaining logs and snapshots; it restores the previous installation if replacement fails.
- **Dedicated ZOS-API thread** — the Worker serializes every connection and tool operation on one long-lived STA thread to respect the COM threading model and avoid cross-thread session access.
- **Long-job control** — POP, global search, and multistart optimization can return immediately with a job id. Use `zemax_job_status`, `zemax_job_list`, and `zemax_job_cancel` for queue position, live progress, result retrieval, and cooperative cancellation; the launcher also shows the active tool/job and elapsed time.
- **Per-client live dashboard** — colour-coded cards distinguish installation, configuration, historical activity, and a real request made within the last five minutes. The launcher health check is excluded from AI activity.
- **Multi-version OpticStudio detection** — detects classic Zemax and current `ANSYS Inc\v*` layouts from environment variables, both registry views, uninstall entries, and known Program Files locations. The launcher validates all three ZOS-API assemblies before offering a version.
- **Cross-version ZOS-API release policy** — the Worker is explicitly x64 for legacy NetHelper compatibility. Release packages are compiled against an explicit oldest-supported OpticStudio/ZOS-API baseline and record that product/API version in `ZOSAPI_BUILD_INFO.txt`. Startup rejects a selected OpticStudio older than the compile baseline before loading Worker code that contains ZOS-API type references. Use `scripts/verify-zosapi-compatibility.ps1` to compile the complete Worker against every actual OpticStudio version that a release claims to support.
- **Safe public package** — the ZIP does not redistribute proprietary ZOS-API DLLs. It uses the licensed OpticStudio installation at runtime on the Zemax computer.

## Reliability and protocol guarantees

MCP `serverInfo.version` is reported by the public Host assembly. The Launcher, Host, Worker, and release `VERSION.txt` are built from the same product version. The structured Worker health RPC independently reports the private RPC version, tool-contract fingerprint, loaded ZOS-API assembly path, actual OpticStudio connection mode, license result, Data directory, snapshots, and job state.

The Host starts independently of the Worker. `tools/list` is served from the static Host manifest and therefore does **not** require the Worker, ZOS-API, an OpticStudio connection, or a valid OpticStudio licence. The first Worker-backed health request or permitted `tools/call` lazily starts the Worker.

The Host creates the current-user-only named pipe before launching the Worker. During startup it authenticates the connecting process using the launched PID and a random per-launch secret, then requires the same private RPC version and exact static-tool-contract SHA-256 fingerprint. This negotiation occurs before the Worker initializes ZOS-API or performs any OpticStudio COM operation, so a mixed or stale Host/Worker package fails clearly instead of executing against a different tool contract. `--worker-startup-timeout-seconds` controls the connection-and-handshake deadline (default **90**, range **10–600**).

The established recovery boundary remains bounded: each normal private RPC write has a **10-second** deadline; a command that exceeds the **300-second** soft timeout immediately starts its hard-recovery deadline and sends a cancellation request with a separately bounded **5-second** pipe-write deadline. The **360-second** hard-recovery deadline is measured from the soft timeout, not after cancellation delivery. A non-responsive Worker is terminated and its pipe generation is invalidated, so the following MCP request starts a clean Worker. Client cancellation ends the HTTP request immediately but leaves a bounded background drain/recovery owner to cancel or restart the Worker.

Browser Origins are configuration-based, never inferred from an incoming `Host` header. Local binding permits only `http://127.0.0.1:*`, `http://localhost:*`, and `http://[::1]:*`. LAN binding requires explicit `--allowed-host` and `--allowed-origin` values. ASP.NET Core Host filtering is enabled with that concrete allowlist, so a spoofed Host header is rejected before it can influence CORS.

### MCP protocol compatibility

The .NET 10 Host uses `ModelContextProtocol.AspNetCore` 2.1.0, so the upstream SDK owns Streamable HTTP negotiation, request IDs, protocol compatibility, SSE, cancellation, and modern stateless behavior. The Worker has no `ModelContextProtocol` dependency and receives only private RPC v3 execution/status/event envelopes.

The static `ZemaxMCP.ToolManifest` is the common contract authority for Host and Worker. It is generated from Worker tool method declarations at build time and contains each tool's name, description, JSON input schema, domain, and impact. The Worker reflection registry only binds JSON arguments to typed C# methods and invokes implementations; it does not generate a second MCP schema.

The separate OpticStudio control lease expires after fifteen minutes without activity unless an operation is active. Identity resolution prefers a dedicated authenticated client profile, then request-scoped `io.zemaxmcp/clientInstanceId`, then `X-Zemax-MCP-Client-Instance`, then a hashed legacy `Mcp-Session-Id`, and finally `clientInfo.name + clientInfo.version + remote IP`. MCP routing headers such as `Mcp-Name` are never treated as client identity. The packaged Claude/stdin proxy emits a fresh instance identifier for every proxy process.

## OpticStudio / ZOS-API version compatibility

Current Ansys ZOS-API documentation is used to verify the exact contract of modern interfaces, but a release does **not** infer old-version compatibility from the newest documentation. ZOS-API functionality has expanded over time, so source/API compatibility for an older release must be proved against that release's real DLLs.

The release direction is therefore explicit:

1. Run `scripts/verify-zosapi-compatibility.ps1` against every installed OpticStudio family that the release intends to support. This only compiles the Worker against those DLLs and does not start OpticStudio or consume a license.
2. Build the release Worker against the **oldest** version that passed the compile matrix by setting `ZEMAX_API_BASELINE_ROOT`.
3. The package records the baseline `OpticStudio.exe` and ZOS-API versions in `ZOSAPI_BUILD_INFO.txt`.
4. At Worker startup, the selected OpticStudio product/API versions are compared with that baseline **before** `ServerApplication` is loaded. An older runtime is rejected explicitly instead of being allowed to fail later with `MissingMethodException`/`TypeLoadException`.
5. Finally run licensed live acceptance on each version family that will be advertised as supported.

Known legacy differences are handled deliberately. Automatic safety snapshots and unsaved multistart checkpoints use `.zmx`, because `.ZOS` did not exist before OpticStudio 21.3. Enhanced Ray Aiming options that evolved during 2021 and became formal in 22.1 are capability-detected; early versions return `null` and `UnsupportedSettings` for unavailable optional fields rather than fabricating values or raising the minimum API for the entire Worker.

See `docs/ZOSAPI_COMPATIBILITY.md` for the current 2021/2023/2024/2026 compatibility matrix and release policy.

## Release validation

Hosted CI validates the public/static contract, safety metadata, Host/private-RPC boundary, recovery paths, desktop packaging, updater rollback, signed-update tamper rejection, and the cross-version ZOS-API policy guards. It also runs functional safety guards that keep global ZOS-API initialization in Worker startup and prohibit ReadOnly analysis tools from structurally modifying the user's Merit Function Editor.

A licensed OpticStudio installation is still required for release acceptance. `scripts/verify-live-mcp.ps1` uses MCP `2026-07-28` stateless requests as its primary path, verifies Host/Worker RPC and manifest agreement, executes a curated live read-only smoke set, and can verify mutation/snapshot safety. `docs/RELEASE_VALIDATION.md` records the staged 126-tool review, old-version compile matrix, and exact live release gate. A green hosted workflow is therefore necessary but is not claimed as proof that every ZOS-API operation has been exercised against a real OpticStudio build.

## Connection modes

| Mode | Intended use |
|---|---|
| **Standalone** | Starts or controls an OpticStudio session for automated work. |
| **Extension** | Connects to an already-running OpticStudio session for interactive work. |

`zemax_connect` compares both the requested mode and Extension instance ID with the current connection. Standalone mode normalizes the irrelevant instance ID to `0`; if the requested connection target differs from the current one, it cleanly disconnects and reconnects. `zemax_status` reports the actual active mode. Use the launcher status dashboard to confirm that ZOS-API is loaded and OpticStudio is connected before asking the AI to work on a design.

For inspection-only sessions, enable **Read-only mode** before connecting the AI. In normal read/write mode, automatic lens snapshots are kept under `%LOCALAPPDATA%\ZemaxMCP\snapshots` (up to the newest 100 files). The status details show the active protection mode, snapshot folder, and most recent snapshot created in the current Worker session.

## Zemax discovery and license diagnostics

The dashboard reports the selected OpticStudio program folder, how it was discovered, the resolved `ZOSAPI.dll`, `ZOSAPI_Interfaces.dll`, and `ZOSAPI_NetHelper.dll` paths, the Zemax Data folder, and license status. After startup it separately reports each assembly's actual CLR load path, so “found” and “loaded” are independently visible. `ZOSAPI_NetHelper.dll` is supported both beside `ZOSAPI.dll` and in the newer `ZOS-API\Libraries` layout.

The Data folder is resolved from `ZEMAX_DATA_ROOT`, the OpticStudio user registry (`HKCU\SOFTWARE\Zemax\ZemaxRoot`), redirected Windows Documents, the normal `Documents\Zemax` location, or the OpticStudio Online default. The presence of `Data\License`, `Data\Configs\SNTLCONFIG.XML`, or `ANSYSLMD_LICENSE_FILE` is shown only as configuration evidence. The authoritative license result is reported after ZOS-API actually connects; secret environment-variable values are never written to logs.

For an unusual portable layout, set `ZEMAX_ROOT` to the program directory and optionally `ZEMAX_DATA_ROOT` to the Data directory before starting the launcher.

## AI client configuration

Use **Configure AI clients** in the launcher. Existing unrelated MCP entries are preserved and a backup is kept when an existing configuration is replaced. Supported HTTP clients receive both the endpoint and its Bearer header; Claude's packaged proxy receives the token without putting it in the server URL. If a token is rotated, reconfigure each client so its saved credential matches.

| Client | Configuration used by the launcher | Connection confirmation |
|---|---|
| Codex | `$CODEX_HOME/config.toml`, or `~/.codex/config.toml` | The client card turns green after an actual request. |
| Claude Desktop | `%APPDATA%/Claude/claude_desktop_config.json`; the packaged local stdio proxy reaches the HTTP/LAN endpoint and provides per-process client identity | The client card turns green after an actual request. |
| Cursor | `~/.cursor/mcp.json` | The client card turns green after an actual request. |
| Google Antigravity | `~/.gemini/config/mcp_config.json` (an existing legacy Antigravity configuration is detected and preserved in place) | The launcher writes Antigravity's remote `serverUrl` plus the Bearer header. Restart Antigravity, then use its MCP Servers panel or `/mcp` to confirm the connection. |
| Kimi Code | `$KIMI_CODE_HOME/mcp.json`, or `~/.kimi-code/mcp.json` | Run `/mcp` in Kimi Code or watch the launcher client card. See the [official Kimi MCP guide](https://www.kimi.com/code/docs/en/kimi-code-cli/customization/mcp.html). |
| WorkBuddy | `~/.workbuddy/mcp.json` | WorkBuddy shows its own green/red MCP status; the launcher also records real requests. See the [official WorkBuddy MCP guide](https://www.workbuddy.ai/docs/zh/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/MCP-Guide). |
| VS Code / Copilot | Native `vscode:mcp/install` review flow; status checks the default and profile-specific `mcp.json` files | Approve the server in VS Code, then make a request. |

Clients capable of setting custom MCP request metadata may send `io.zemaxmcp/clientInstanceId`; HTTP clients may instead send `X-Zemax-MCP-Client-Instance`. The value must be 1–128 ASCII letters/digits plus `.`, `_`, or `-`. This is useful when multiple independent client instances share the same IP and the same standard `clientInfo`.
