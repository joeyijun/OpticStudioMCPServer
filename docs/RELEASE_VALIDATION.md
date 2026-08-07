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
- syntax and protocol-shape validation of the live release verifier;
- functional safety guards for Worker-owned ZOS initialization, read-only analysis boundaries, reviewed MCE mutation/readback contracts, and Glasscat path/data-integrity rules.

Hosted CI deliberately does not claim that a ZOS-API call works against a real OpticStudio build because proprietary ZOS-API assemblies and a valid license are not available on the runner. Where a reviewed tool uses version-sensitive ZOS-API members, the implementation is checked against the current Ansys ZOS-API reference and remains subject to licensed live acceptance.

## 2. Live smoke test: real OpticStudio

Run this later on a Windows machine with the release candidate installed and a valid OpticStudio license:

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

Run the live verifier with safety checks in both modes when licensed-machine acceptance is performed.

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

| Stage | Functional area | Review status | Release focus |
| --- | --- | --- | --- |
| A | System/session | Static review complete | connect, status, open/new/save, restart/disconnect, path handling, unsaved-work semantics |
| B | Sequential editing | Core static review complete | surfaces, solves, fields, wavelengths, stop, aperture, vignetting, system settings |
| C | Structured read-only analysis | Static review complete | ray trace/fans, spot, MTF, PSF, aberrations, illumination, encircled energy, GIA, first-order data |
| D | Configuration/catalog | In progress | MCE configuration editing/readback, glass catalog parsing/filtering/export |
| E | Optimization | Not started | merit function, variables, local/global/hammer/multistart, cancellation/jobs |
| F | Specialized | Not started | POP, non-sequential inspection, tolerancing, generic analysis/file exports |

“Static review complete” means no known P0/P1 contract finding remains from repository/API inspection and the reviewed invariant is guarded where practical. It does **not** replace the licensed OpticStudio acceptance run, especially for version-dependent analysis text/grid formats.

For each public tool, review the following contract:

1. **Input schema** — required fields, default values, enum vocabulary, units, 0/1-based indexing, null/empty semantics.
2. **Validation** — reject NaN/infinity, invalid ranges, invalid indices, impossible enum values, and contradictory bounds before COM mutation.
3. **Bidirectionality** — nullable setters must distinguish omitted values from explicit `false`, `0`, and empty strings when those are meaningful values.
4. **Safety classification** — ReadOnly/Caution/HighImpact must match actual behavior; recognized mutations must go through a named `ExecuteAsync` command so snapshot policy cannot be bypassed.
5. **Cancellation** — operations that can wait or queue should accept the injected `CancellationToken` and pass it to session/job APIs where meaningful.
6. **Result truthfulness** — `Success=true` must describe the primary Zemax operation, not an unrelated sidecar/logging step; partial auxiliary failures should be warnings.
7. **Readback** — mutating tools should return the value read back from OpticStudio where possible rather than merely echoing requested input.
8. **Atomicity** — validation should happen before mutation; when a multi-step mutation can fail after the first write, rollback or an explicit partial-failure contract is required.
9. **Version compatibility** — optional/newer ZOS-API members need explicit fallback/warning behavior rather than an unexplained runtime binder failure.
10. **Filesystem/data integrity** — a tool must not escape its documented directory, silently use only a subset of requested sources, or convert malformed external data into plausible default values.

## 5. Current functional review fixes

### Stage A — System/session

- Worker startup is the single owner of `ZOSAPI_Initializer.Initialize()` after private-contract negotiation. `ZemaxSession.ConnectCore()` owns only application connection/reconnection and no longer repeats global ZOS-API initialization.
- `zemax_connect` normalizes standalone instance IDs to `0`, rejects negative extension IDs, and propagates cancellation; irrelevant standalone `instanceId` values can no longer cause a false reconnect target change.
- `zemax_restart` propagates cancellation through its delay and reconnect path.
- `zemax_new_system` no longer records a second HighImpact `NewSystem` operation merely to read the resulting surface count; readback uses `GetSystem`.
- `zemax_open_file` validates and normalizes the path, propagates cancellation, and records post-open inspection as `GetSystem` rather than a duplicate OpenFile operation.
- `zemax_save_file` treats the Zemax file as the authoritative save result. A constraint-sidecar failure is reported as a warning after a successful lens save instead of falsely reporting that the lens file failed to save.
- Quick Focus and Scale Lens accept only the documented criterion/unit vocabularies rather than numeric enum strings and propagate cancellation to the session boundary.

### Stage B — Sequential editing

- `zemax_set_surface` allows explicit empty strings to clear material/comment values, explicit `false` to clear stop status, and explicit `false` to return radius/thickness/conic solves to Fixed. Omission still means leave unchanged.
- `zemax_set_surface` rejects contradictory thickness bounds and propagates cancellation to the session dispatcher.
- `zemax_set_surface_solve` validates finite numeric inputs, pupil zone `0..1`, non-negative pickup columns, positive supplied F-number, and pickup/reference surface ranges before applying a solve; cancellation is propagated.
- `zemax_set_extra_data` rejects non-positive XDAT cells and NaN/infinite values in single and batch writes before touching cells; cancellation is propagated.
- `zemax_set_surface_parameter` validates PARM `1..20` and finite values before access. Pure read mode uses the explicitly ReadOnly `GetSurfaceParameter` execution command instead of triggering a HighImpact SetSurfaceParameter snapshot.
- `zemax_set_surface_type` keeps `listTypes=true` only as a compatibility path and returns the static enum before entering the HighImpact session operation. Actual mutation accepts only named enum members, not numeric enum strings.
- `zemax_set_surface_aperture` validates the documented aperture vocabulary and finite/radius constraints before entering the mutation path; getter and setter propagate cancellation.
- Field, wavelength, system-aperture, and vignetting operations propagate cancellation through the session dispatcher.
- Polarization method selection accepts only `XAxisMethod`, `YAxisMethod`, or `ZAxisMethod`, preventing undocumented numeric enum values from passing `Enum.TryParse`.

### Stage C — structured read-only analysis

- `zemax_ray_trace` and `zemax_ray_trace_extended` validate normalized Hx/Hy/Px/Py in `[-1,1]`, wavelength and surface ranges, propagate cancellation, and report success only when the ray-trace API succeeds with error code zero.
- `zemax_ray_fan`, `zemax_opd_fan`, and `zemax_pupil_aberration_fan` require results/text plus complete tangential and sagittal field sections with consistent wavelength/data dimensions. Version-dependent parser failures are explicit instead of being converted into zero-valued curves.
- `zemax_spot_diagram`, `zemax_rms_spot`, and `zemax_cardinal_points` no longer insert temporary operands into the user's Merit Function Editor. They use `IMeritFunctionEditor.GetOperandValue`; CI rejects structural MFE writes anywhere under `Tools/Analysis`.
- Spot-diagram field normalization follows rectangular versus radial field-normalization semantics instead of applying one radial-style denominator to both modes.
- `zemax_fft_mtf`, `zemax_geometric_mtf`, `zemax_fft_mtf_vs_field`, and `zemax_geometric_mtf_vs_field` reject invalid frequencies/wavelengths/sampling instead of silently retaining defaults. Missing settings/results/text and malformed or empty sections are failures, not successful empty/NaN/zero-filled results.
- `zemax_fft_psf` and `zemax_huygens_psf` use strict named enum/range/result contracts, reject silent fallback, propagate cancellation, and report version-sensitive optional-setting/text-export limitations explicitly.
- `zemax_seidel_coefficients`, `zemax_chromatic_focal_shift`, `zemax_longitudinal_aberration`, `zemax_lateral_color`, and `zemax_field_curvature_distortion` require complete parseable primary data before success; absent optional metadata is not fabricated as zero.
- `zemax_relative_illumination` requires a non-empty parseable field/illumination/effective-F-number table and propagates cancellation.
- `zemax_diffraction_encircled_energy` and `zemax_geometric_encircled_energy` validate sampling, require real results/text and complete data blocks, and propagate cancellation. The legacy geometric `scaleByDiffractionLimit` parameter is retained only for compatibility and rejects `true`, because the verified ZOS-API interface has no such setting.
- `zemax_aperture_throughput` validates normalized field, wavelength and surface ranges, checks cancellation inside the pupil-ray loop, distinguishes ray-trace errors from aperture/vignette loss, and computes clear fraction over successfully traced rays.
- `zemax_geometric_image_analysis` now uses the typed `IAS_GeometricImageAnalysis` contract. Because it is classified ReadOnly, it no longer persists `IMA.CFG` or writes TXT/BMP files; those operations belong to the HighImpact export tool. Structured results are limited to grid-producing modes and clearly describe the legacy peak/sum field semantics.

Stage C static review is complete for the structured ReadOnly analysis set. `zemax_pop` and generic filesystem export are deliberately reviewed in Stage F. Licensed-machine acceptance is still required for version-dependent text/grid behavior before release.

### Stage D — configuration and glass catalog (in progress)

- `zemax_add_configuration_operand` validates a named `MultiConfigOperandType` before creating a row, rejects numeric enum strings, checks `ChangeType`, applies only enabled parameters, and attempts rollback if post-insert setup fails. This closes the prior failure mode where an invalid type returned `Success=false` after leaving an extra MCE row behind.
- `zemax_set_current_configuration`, `zemax_delete_configuration_operand`, and configuration-count changes check the official MCE success return values and verify resulting state/count instead of assuming mutation succeeded.
- `zemax_get_configuration_operands` preserves the actual MCE cell `Double`/`Integer`/`String` data type instead of assuming every operand cell is numeric. ConfigPickup readback includes source configuration, source operand, and supported scale/offset values.
- `zemax_set_configuration_operand_value` makes fixed double/integer/string and ConfigPickup modes mutually exclusive, validates finite values and source ranges, uses typed `CreateSolveType(SolveType.ConfigPickup)` / `_S_ConfigPickup`, checks `SetSolveData` / `MakeSolveFixed`, and returns typed readback. It can therefore represent string-valued operands such as glass selection without coercing them through `DoubleValue`.
- Glass filtering validates finite values, non-negative distance weights/radius/cost, min/max ordering, positive wavelength coverage and melt-frequency bounds before evaluating the catalog.
- `zemax_get_glasses`, `zemax_filter_glasses`, and `zemax_export_glass_catalog` require every requested source catalog to exist. They no longer silently build a partial result from the subset of correctly spelled sources.
- AGF parsing fails with catalog name and line number when a provided numeric token is malformed; invalid source data is no longer silently converted to a plausible zero value.
- Glass export treats `catalogName` as a file name rather than a path, rejects Windows-invalid/reserved names and path separators, verifies the final canonical path remains under the Zemax `Glasscat` directory, and writes through a same-directory temporary file before replace/move to reduce partial-overwrite risk.

Remaining Stage D work is to finish the cross-check of catalog parsing/export behavior and MCE edge cases, then add any focused ZOS-independent fixtures that can prove the above contracts without OpticStudio. Live validation of actual MCE/AGF behavior remains part of the deferred licensed-machine acceptance.

## 6. Release gate

A build is release-ready only when all of the following are true:

- GitHub Windows CI is green at the exact release-candidate commit;
- the live 2026-07-28 verifier passes against a licensed OpticStudio installation;
- safety verification passes in the intended release mode(s);
- Host/Worker RPC version and manifest fingerprint agree in live health;
- no P0/P1 findings remain in every functional review stage declared complete for the release;
- the release ZIP/update signature and rollback checks pass;
- the tested OpticStudio version(s) are recorded in the release notes.
