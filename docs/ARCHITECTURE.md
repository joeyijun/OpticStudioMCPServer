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

### Shared

- `src/ZemaxMCP.Core` owns ZOS-independent execution abstractions, session safety, STA dispatch, optimization helpers and shared services.
- `src/ZemaxMCP.Documentation` contains documentation/search data used by Worker tools.

### Contracts

- `src/ZemaxMCP.Rpc/Protocol` contains private RPC framing/version/handshake types.
- `src/ZemaxMCP.Rpc/Contracts` contains execution, status, event and error DTOs. It contains no MCP or ZOS-API types.
- `src/ZemaxMCP.ToolManifest` contains the generated public tool contract and explicit tool policy metadata shared by Host and Worker.
- `tools/ZemaxMCP.ToolManifestGenerator` is a build-only Roslyn generator. It is intentionally not a separately built solution project; `ZemaxMCP.ToolManifest` owns its invocation.

### Desktop/package

- `src/ZemaxMCP.Launcher` owns end-user setup, configuration, status and service lifecycle.
- `src/ZemaxMCP.ClientProxy` adapts stdio-only clients to the public HTTP MCP endpoint and emits a per-process client instance identity.
- `src/ZemaxMCP.Installer` and `src/ZemaxMCP.Updater` own installation and verified update flows.

## Tool contract ownership

Worker tool methods remain the authoring source for tool names, descriptions and parameter shapes. At build time the manifest generator produces a static contract containing all 126 tools. Each entry includes:

- stable MCP tool name
- description
- JSON input schema
- explicit domain
- explicit impact level

`StaticToolManifest.ContractFingerprint` is a deterministic SHA-256 digest over those fields. The Host and Worker compare the fingerprint, RPC version, Worker PID and per-launch secret during the private-pipe handshake before the Worker loads ZOS-API. A mixed or stale package fails startup rather than silently executing against a different contract.

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

Tool arguments remain manifest-defined JSON between Host and Worker, while RPC infrastructure/status/event envelopes are strongly typed. This avoids maintaining 126 duplicate per-tool RPC DTOs while still providing a compile-time typed infrastructure boundary.

## Progress and event dispatch

Worker job/snapshot callbacks never write directly to the pipe. They enqueue structured events into a single Worker outbound event queue; one pump serializes them through the pipe writer.

The Host pipe reader only parses and routes frames. Progress and snapshot frames are moved to an independent Host event channel so slow consumers cannot block result/cancellation processing. The Host:

- retains recent structured job progress for diagnostics;
- retains the most recent snapshot path;
- forwards matching operation progress through MCP only when the original MCP request supplied a progress token.

Background jobs that outlive the original MCP request remain observable through job/status state rather than attempting to send progress against a completed request.

## Client identity and control lease

OpticStudio ownership is independent of MCP transport sessions. Identity is resolved in this order:

1. a dedicated authenticated client profile, when provisioned;
2. request-scoped `io.zemaxmcp/clientInstanceId` metadata;
3. validated `X-Zemax-MCP-Client-Instance` HTTP header;
4. hashed legacy `Mcp-Session-Id`;
5. `clientInfo.name + clientInfo.version + remote IP` fallback.

Instance identifiers are 1-128 ASCII letters/digits plus `.`, `_` and `-`. The packaged stdio proxy generates a new identifier per proxy process. Clients that support custom MCP request metadata or HTTP headers can provide their own stable/process-specific identifier.

Do not use `Mcp-Name` as client identity; it is routing metadata for the invoked tool/resource/prompt.

## Dependency rules

Keep these rules fail-closed in CI:

- only the Host may reference `ModelContextProtocol` / `ModelContextProtocol.AspNetCore`;
- the Worker and private RPC projects must not reference MCP SDK types;
- the private RPC project must not reference ZOS-API;
- public tool discovery must not call Worker RPC;
- execution admission must be checked both in the Host and Worker using the shared manifest;
- new Worker tools must have explicit domain and impact metadata;
- generated schemas may not silently degrade to unresolved opaque object contracts.
