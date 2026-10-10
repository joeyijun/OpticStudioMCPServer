# OpticStudio MCP architecture

This document defines the ownership boundaries that keep MCP transport logic, the public tool contract, and OpticStudio/ZOS-API execution independent.

## Runtime boundary

```text
MCP client
   |
   | Streamable HTTP
   v
ZemaxMCP.Host (.NET 10)
   |  - official ModelContextProtocol.AspNetCore transport
   |  - authentication, Origin/Host filtering
   |  - client identity and OpticStudio control lease
   |  - static tools/list and tool admission
   |  - progress forwarding and Worker health aggregation
   |
   | private Named Pipe RPC v3
   | PID + launch secret + manifest fingerprint negotiation
   v
ZemaxMCP.Worker (.NET Framework 4.8)
      - one ZOS-API process/session
      - one serialized tool execution boundary
      - command binder and safety checks
      - snapshots, jobs, progress and status events
      - no MCP transport or MCP SDK dependency
```

The Host may start and answer `tools/list` without starting the Worker. The Worker is lazy-started only for Worker-backed status or execution.

## Project ownership

### Runtime

- `src/ZemaxMCP.HttpBridge` builds `ZemaxMCP.Host`. The historical directory name is retained to avoid breaking packaging/update paths; the solution display name is `ZemaxMCP.Host`.
- `src/ZemaxMCP.Server` builds `ZemaxMCP.Worker` and is the only executable project allowed to depend on proprietary ZOS-API assemblies.
- `BootstrapProgram` owns early CLR binding for the three ZOS-API assemblies. `ServerApplication` owns the authenticated private-RPC handshake and does not register a second resolver.

### Shared

- `src/ZemaxMCP.Core` owns ZOS-independent execution abstractions, session safety, STA dispatch, optimization helpers and shared services.
- `src/ZemaxMCP.Documentation` contains documentation/search data used by Worker tools.

### Contracts

- `src/ZemaxMCP.Rpc/Protocol` contains private RPC framing/version/handshake types.
- `src/ZemaxMCP.Rpc/Contracts` contains execution, status, event and error DTOs. It contains no MCP or ZOS-API types.
- `src/ZemaxMCP.ToolManifest` contains the generated public tool contract and explicit tool policy metadata shared by Host and Worker.
- `tools/ZemaxMCP.ToolManifestGenerator` is a build-only Roslyn generator. It is intentionally not a separately built solution project; `ZemaxMCP.ToolManifest` owns its invocation.

### Desktop/package

- `src/ZemaxMCP.Launcher` owns end-user setup, configuration, status and service lifecycle. The WPF `MainWindow` owns visible UI state and service-start/stop interactions; `ClientConfigurator` owns per-client config-file formats and detection; `McpDiagnosticsClient` owns HTTP MCP health/protocol smoke probes; `JobActionClient` owns authenticated Job status/cancellation RPC; `TaskPresentation` owns UI-independent Task/Job projections; and `ScopedDeltaCursor` owns credential-bound polling cursors. These are incremental separations, **not yet a complete WPF MVVM conversion**.
- `src/ZemaxMCP.ClientProxy` adapts stdio-only clients to the public HTTP MCP endpoint and emits a per-process client instance identity.
- `src/ZemaxMCP.Installer` owns first-install UI and delegates upgrades to `src/ZemaxMCP.Updater`; portable upgrades use the same updater replacement/rollback path when an installed copy exists.

## Tool contract ownership

Worker tool methods remain the authoring source for tool names, descriptions and parameter shapes. At build time the manifest generator produces a static contract containing all 144 tools. Each entry includes:

- stable MCP tool name
- description
- JSON input schema
- explicit domain
- explicit impact level

`StaticToolManifest.ContractFingerprint` is a deterministic SHA-256 digest over those fields. The Host and Worker compare the fingerprint, RPC version, Worker PID and per-launch secret during the private-pipe handshake before the Worker initializes ZOS-API or begins any OpticStudio COM operation. A mixed or stale package fails startup rather than silently executing against a different contract.

The same manifest backs `tools/list`, Host direct-call admission, Worker RPC admission and `zemax_tool_catalog`. The Worker reflection registry binds and invokes implementations but does not generate a second schema.

## Private RPC v3

RPC v3 deliberately has no discovery command. Its request/response surface is limited to execution and runtime state:

- `invoke-tool`
- `cancel-operation`
- `get-status`
- `progress`
- `snapshot-created`
- `result`
- `error`

Tool arguments remain manifest-defined JSON between Host and Worker, while RPC infrastructure/status/event envelopes are strongly typed. This avoids maintaining 144 duplicate per-tool RPC DTOs while still providing a compile-time typed infrastructure boundary.

## Progress and event dispatch

Worker job/snapshot callbacks never write directly to the pipe. Snapshot notifications remain FIFO/durable, while progress is latest-state data and is coalesced by Job ID before the Worker event pump serializes it through the pipe writer.

The Host pipe reader only parses and routes frames. It records snapshot creation immediately and independently coalesces progress by Worker generation + Job ID, so a slow MCP progress consumer cannot make memory usage grow with update frequency. The Host:

- retains bounded recent structured job state for diagnostics;
- retains the most recent snapshot path;
- forwards matching operation progress through MCP only when the original MCP request supplied a progress token;
- correlates a background Job with the Worker operation that created it.

Background jobs that outlive the original MCP request remain observable through job/status state. Queue length, metadata history, and large result-payload history have separate bounds. Cancellation has an independent drain deadline; if a cancelled ZOS-API job cannot stop within that grace period, the Worker generation is terminated so the Host can start cleanly.

## Cached Job/Task delta diagnostics

`GET /mcp/jobs-delta` returns HMAC cursor-based changes for the already
cached Host Job state and latest generation-bound progress events. It
does **not** send a new Worker status RPC or block on COM. Scoped
bearer credentials receive only their authenticated owned Job IDs
and safe Task metadata; uncached just-registered Jobs have explicit
`Unknown` state. Global Worker busy/freshness cannot affect another
scoped owner's cursor. Launcher polls this endpoint at one-second
cadence, resets the opaque cursor when either endpoint **or bearer
credential** changes, and falls back to five-second `/health` for
older Hosts. Never confuse this cached state with a verified fresh
ZOS-API numeric or physical observation.

## Long-running optical tools

Long-running NSC tracing and sequential tolerancing use the same bounded Worker Job lifecycle as optimization. The synchronous implementation owns the ZOS-API tool, polls through `ISystemTool.WaitWithTimeout`, cooperatively cancels and drains on caller cancellation/timeout, and always closes the tool before the next COM operation. Background mode wraps that same implementation in `McpJobManager`; it does not create a second execution path.

Tolerancing result extraction is structured rather than text-parser-first: the Worker requests a temporary ZTD data-retention file, closes Tolerancing, opens the official Tolerance Data Viewer, reads Monte Carlo column metadata/statistics and Sensitivity data, then deletes the temporary ZTD. Monte Carlo lens files are explicitly disabled.

## Client identity and control lease

OpticStudio ownership is independent of MCP transport sessions. In opt-in multi-client authentication mode, the Host authenticates a unique bearer-token digest and uses the server-controlled credential ID as lease identity. Each request reloads credential hashes to allow immediate revocation; a scoped read-only credential exposes only ReadOnly-impact tools in both tools/list and tools/call. This is an authorization boundary, not confidential multi-tenant isolation for Job history or optical files. See [per-client authentication](MULTI_CLIENT_AUTH.md). An owned background Job keeps the control lease alive beyond the normal idle timeout and is bound to the Worker generation that created it; terminal Job state or generation replacement releases the hold. In the P1 development branch, ordinary ReadOnly tools instead use a **serialized, non-owning observation lease**: they may inspect the stateful model without taking or renewing a writer's persistent lease, but they cannot interleave within a single ZOS-API call. A foreign client is still denied an observation while an active background optical Job holds control. This is not a consistent multi-call snapshot or confidential lens multi-tenancy; mutations and long-running Jobs remain owner-exclusive. Identity is resolved in this order:

1. a dedicated authenticated client profile, when provisioned;
2. request-scoped `io.zemaxmcp/clientInstanceId` metadata;
3. validated `X-Zemax-MCP-Client-Instance` HTTP header;
4. hashed legacy `Mcp-Session-Id`;
5. `clientInfo.name + clientInfo.version + remote IP` fallback.

Instance identifiers are 1-128 ASCII letters/digits plus `.`, `_` and `-`. The packaged stdio proxy generates a new identifier per proxy process. Clients that support custom MCP request metadata or HTTP headers can provide their own stable/process-specific identifier.

Do not use `Mcp-Name` as client identity; it is routing metadata for the invoked tool/resource/prompt.

## Retired migration assets

The old Worker-owned MCP prompts/resources and JSON appsettings were intentionally removed after the public SDK Host migration. They are recoverable from Git history if those capabilities are deliberately reintroduced, but they are not kept as excluded/dead source in the active Worker tree.

## Dependency rules

Keep these rules fail-closed in CI:

- only the Host may reference `ModelContextProtocol` / `ModelContextProtocol.AspNetCore`;
- the Worker and private RPC projects must not reference MCP SDK types;
- the private RPC project must not reference ZOS-API;
- public tool discovery must not call Worker RPC;
- execution admission must be checked both in the Host and Worker using the shared manifest;
- new Worker tools must have explicit domain and impact metadata;
- generated schemas may not silently degrade to unresolved opaque object contracts.
