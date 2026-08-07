# Release validation

This document defines the release-candidate validation sequence for OpticStudioMCPServer. The goal is to separate checks that can run on GitHub-hosted Windows runners from tests that require a licensed OpticStudio installation.

## 1. Hosted CI: contract and architecture

Every pull request must pass the normal Windows workflow before any live OpticStudio acceptance run.

The hosted checks cover:

- 109 Worker tool classes / 126 unique commands;
- explicit domain and impact metadata for every public tool;
- generated JSON schemas and the deterministic static-manifest fingerprint;
- official .NET 10 MCP Host behavior and 2026-07-28 stateless requests;
- private RPC v3 authentication, contract negotiation, cancellation/recovery, and event dispatch;
- Host-only `tools/list` and Worker lazy start;
- client-instance identity and control-lease isolation;
- updater rollback and signed-update tamper rejection;
- syntax and protocol-shape validation of the live release verifier.

Hosted CI deliberately does not claim that a ZOS-API call works against a real OpticStudio build because proprietary ZOS-API assemblies and a valid license are not available on the runner.

## 2. Live smoke test: real OpticStudio

Run this on a Windows machine with the release candidate installed and a valid OpticStudio license:

```powershell
$env:ZEMAX_MCP_TOKEN = "<token from Copy secure setup>"
./scripts/verify-live-mcp.ps1 -Endpoint "http://127.0.0.1:8000/mcp"
```

The live verifier uses MCP `2026-07-28` stateless requests as the primary protocol. It first calls `tools/list`, then starts the Worker through the health/status path. It checks:

- public tool names are unique and schemas are object contracts;
- required/optional semantics for key editing tools have not regressed;
- Host and Worker private-RPC versions match;
- Host and Worker manifest fingerprints match;
- the active toolset is reported;
- `zemax_tool_catalog` and public `tools/list` expose the same policy-visible count;
- a curated set of system/sequential read-only tools executes successfully against the live system.

For a release candidate that must retain initialize-era compatibility, add:

```powershell
./scripts/verify-live-mcp.ps1 -VerifyLegacyCompatibility
```

This performs an explicit `2025-11-25` initialize probe after the modern stateless checks. Legacy compatibility is not used as the primary release path.

## 3. Safety acceptance

Run the live verifier with safety checks in both modes.

Read-only mode:

```powershell
./scripts/verify-live-mcp.ps1 -VerifySafety
```

The verifier reads current metadata, attempts to write the same values back, and requires the Host policy to reject the mutating tool before Worker/ZOS-API execution. No new snapshot may appear.

Read/write mode:

```powershell
./scripts/verify-live-mcp.ps1 -VerifySafety
```

The same no-op metadata write must succeed and must create a new verified `.zos` pre-change snapshot. The optical metadata itself is not intentionally changed.

## 4. Functional review order

The 126 tools are reviewed in this order so release-critical editing and recovery paths are validated before specialized analyses.

| Stage | Functional area | Release focus |
| --- | --- | --- |
| A | System/session | connect, status, open/new/save, restart/disconnect, path handling, unsaved-work semantics |
| B | Sequential editing | surfaces, solves, fields, wavelengths, stop, aperture, vignetting, system settings |
| C | Read-only analysis | ray trace, spot, MTF, PSF, aberrations, illumination, first-order data |
| D | Configuration/catalog | MCE configuration editing, glass catalog inspection/export |
| E | Optimization | merit function, variables, local/global/hammer/multistart, cancellation/jobs |
| F | Specialized | POP, non-sequential inspection, tolerancing, exports |

For each public tool, review the following contract:

1. **Input schema** — required fields, default values, enum vocabulary, units, 0/1-based indexing, null/empty semantics.
2. **Validation** — reject NaN/infinity, invalid ranges, invalid indices, impossible enum values, and contradictory bounds before COM mutation.
3. **Bidirectionality** — nullable setters must distinguish omitted values from explicit `false`, `0`, and empty strings when those are meaningful values.
4. **Safety classification** — ReadOnly/Caution/HighImpact must match actual behavior; recognized mutations must go through a named `ExecuteAsync` command so snapshot policy cannot be bypassed.
5. **Cancellation** — operations that can wait or queue should accept the injected `CancellationToken` and pass it to session/job APIs where meaningful.
6. **Result truthfulness** — `Success=true` must describe the primary Zemax operation, not an unrelated sidecar/logging step; partial auxiliary failures should be warnings.
7. **Readback** — mutating tools should return the value read back from OpticStudio where possible rather than merely echoing requested input.
8. **Version compatibility** — optional/newer ZOS-API members need explicit fallback/warning behavior rather than an unexplained runtime binder failure.

## 5. Current Stage A/B fixes

The first pass identified and corrected several release-relevant issues.

### System/session

- Worker startup is the single owner of `ZOSAPI_Initializer.Initialize()` after private-contract negotiation. `ZemaxSession.ConnectCore()` now owns only application connection/reconnection and no longer repeats global ZOS-API initialization.
- `zemax_connect` normalizes standalone instance IDs to `0`, rejects negative extension IDs, and propagates cancellation; irrelevant standalone `instanceId` values can no longer cause a false reconnect target change.
- `zemax_restart` propagates cancellation through its delay and reconnect path.
- `zemax_new_system` no longer records a second HighImpact `NewSystem` operation merely to read the resulting surface count; readback uses `GetSystem`.
- `zemax_open_file` validates and normalizes the path, propagates cancellation, and records post-open inspection as `GetSystem` rather than a duplicate OpenFile operation.
- `zemax_save_file` treats the Zemax file as the authoritative save result. A constraint-sidecar failure is reported as a warning after a successful lens save instead of falsely reporting that the lens file failed to save.
- Quick Focus and Scale Lens now accept only the documented criterion/unit vocabularies rather than numeric enum strings and propagate cancellation to the session boundary.

### Sequential editing

- `zemax_set_surface` now allows explicit empty strings to clear material/comment values, explicit `false` to clear stop status, and explicit `false` to return radius/thickness/conic solves to Fixed. Omission still means leave unchanged.
- `zemax_set_surface` rejects contradictory thickness bounds and propagates cancellation to the session dispatcher.
- `zemax_set_surface_solve` validates finite numeric inputs, pupil zone `0..1`, non-negative pickup columns, positive supplied F-number, and pickup/reference surface ranges before applying a solve; cancellation is propagated.
- `zemax_set_extra_data` rejects non-positive XDAT cells and NaN/infinite values in single and batch writes before touching cells; cancellation is propagated.
- `zemax_set_surface_parameter` validates PARM `1..20` and finite values before access. Pure read mode now uses the explicitly ReadOnly `GetSurfaceParameter` execution command instead of triggering a HighImpact SetSurfaceParameter snapshot.
- `zemax_set_surface_type` keeps `listTypes=true` only as a compatibility path and returns the static enum before entering the HighImpact session operation. Actual mutation accepts only named enum members, not numeric enum strings.
- `zemax_set_surface_aperture` validates the documented aperture vocabulary and finite/radius constraints before entering the mutation path; getter and setter propagate cancellation.
- Field, wavelength, system-aperture, and vignetting operations now propagate cancellation through the session dispatcher.
- Polarization method selection now accepts only `XAxisMethod`, `YAxisMethod`, or `ZAxisMethod`, preventing undocumented numeric enum values from passing `Enum.TryParse`.

These fixes still require the live acceptance pass on at least one supported OpticStudio installation before the release candidate is promoted.

## 6. Release gate

A build is release-ready only when all of the following are true:

- GitHub Windows CI is green at the exact release-candidate commit;
- the live 2026-07-28 verifier passes against a licensed OpticStudio installation;
- safety verification passes in the intended release mode(s);
- Host/Worker RPC version and manifest fingerprint agree in live health;
- no P0/P1 findings remain in the System/session and Sequential-editing review;
- the release ZIP/update signature and rollback checks pass;
- the tested OpticStudio version(s) are recorded in the release notes.
