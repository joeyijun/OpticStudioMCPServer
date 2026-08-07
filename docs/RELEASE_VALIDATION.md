# Release validation

This document defines the release-candidate validation sequence for OpticStudioMCPServer. It deliberately separates repository/contract checks that run on GitHub-hosted Windows runners from acceptance tests that require a licensed OpticStudio installation.

## 1. Hosted CI: contract and architecture

Every pull request must pass the Windows workflow at the exact release-candidate commit before licensed-machine acceptance.

Hosted checks cover:

- 109 Worker tool classes / 126 unique commands;
- explicit domain and impact metadata for every public tool;
- generated JSON schemas and deterministic static-manifest fingerprint;
- official .NET 10 MCP Host behavior and MCP 2026-07-28 stateless requests;
- private RPC v3 authentication, contract negotiation, cancellation/recovery, and event dispatch;
- Host-only `tools/list` and lazy Worker startup;
- client-instance identity and OpticStudio control-lease isolation;
- updater rollback and signed-update tamper rejection;
- syntax/protocol-shape validation of the live release verifier;
- functional-safety guards covering reviewed Stage A-F contracts;
- dedicated Stage E optimization guards for transactional MFE changes, configuration-aware MCE variable addressing, typed merit reads, and cancellation;
- dedicated Stage F guards for POP, NSC, tolerancing, BMP rendering, and generic analysis exports.

Hosted CI deliberately does **not** claim that a ZOS-API call works against a real OpticStudio build. Proprietary ZOS-API assemblies and a valid OpticStudio license are not available on the hosted runner. Version-sensitive ZOS-API members are checked against the current Ansys reference and must still pass the licensed-machine acceptance defined below.

## 2. Live smoke test: real OpticStudio

Run this later on a Windows machine with the release candidate installed and a valid OpticStudio license:

```powershell
$env:ZEMAX_MCP_TOKEN = "<token from Copy secure setup>"
./scripts/verify-live-mcp.ps1 -Endpoint "http://127.0.0.1:8000/mcp"
```

The live verifier uses MCP `2026-07-28` stateless requests as the primary protocol. It checks:

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

This performs an explicit `2025-11-25` initialize probe after the modern stateless checks. Legacy compatibility is not the primary release path.

## 3. Safety acceptance

Run the live verifier with safety checks in the intended release mode(s) when licensed-machine acceptance is performed.

Read-only mode:

```powershell
./scripts/verify-live-mcp.ps1 -VerifySafety
```

A no-op mutation must be rejected by policy before Worker/ZOS-API execution and no new snapshot may appear.

Read/write mode:

```powershell
./scripts/verify-live-mcp.ps1 -VerifySafety
```

The same no-op mutation must succeed and create a verified `.zos` pre-change snapshot. The optical metadata itself is not intentionally changed.

## 4. Functional review order and current status

The 126 public commands are reviewed in stages so release-critical editing/recovery paths are checked before specialized analyses.

| Stage | Functional area | Review status | Release focus |
| --- | --- | --- | --- |
| A | System/session | Static review complete | connect, status, open/new/save, restart/disconnect, paths, unsaved-work semantics |
| B | Sequential editing | Core static review complete | surfaces, solves, fields, wavelengths, stop, aperture, vignetting, system settings |
| C | Structured read-only analysis | Static review complete | ray trace/fans, spot, MTF, PSF, aberrations, illumination, encircled energy, GIA |
| D | Configuration/catalog | Static review complete | MCE editing/readback, typed configuration cells, AGF parsing/filtering/export |
| E | Optimization | Static review complete | MFE, variables, constraints, local/global/hammer/multistart, cancellation/jobs |
| F | Specialized | Static review complete | POP, NSC inspection, tolerancing, BMP/TXT/ZBF/raw-grid exports |

“Static review complete” means no known P0/P1 contract finding remains from repository/API inspection and the important reviewed invariants are guarded where practical. It does **not** replace licensed OpticStudio acceptance, especially for version-dependent analysis settings, text layouts, grids, file exports, tool lifecycles, and COM behavior.

For each public tool, review:

1. **Input schema** — required fields, defaults, enum vocabulary, units, indexing, null/empty semantics.
2. **Validation** — reject NaN/infinity, invalid ranges/indices/enums, and contradictory bounds before mutation.
3. **Bidirectionality** — nullable setters distinguish omitted values from explicit `false`, `0`, and empty strings where meaningful.
4. **Safety classification** — ReadOnly/Caution/HighImpact matches actual side effects and mutations go through named session commands.
5. **Cancellation** — long/queued operations propagate the injected `CancellationToken` through session/job/algorithm loops where meaningful.
6. **Result truthfulness** — `Success=true` describes the requested primary operation; missing/invalid primary data is not fabricated as zero/empty success.
7. **Readback** — mutating tools read back applied values where the ZOS-API supports it.
8. **Atomicity** — validation precedes mutation; multi-step writes use rollback or explicit partial-failure contracts.
9. **Version compatibility** — version-sensitive members fail explicitly instead of silently retaining defaults.
10. **Filesystem/data integrity** — documented path boundaries, source completeness, no-clobber behavior, and malformed-data rejection are enforced.

## 5. Current functional review fixes

### Stage A — System/session

- Worker startup is the single owner of `ZOSAPI_Initializer.Initialize()` after private-contract negotiation; session reconnects no longer repeat global ZOS initialization.
- `zemax_connect` normalizes standalone instance IDs, rejects negative extension IDs, and propagates cancellation.
- `zemax_restart` propagates cancellation through delay/reconnect.
- `zemax_new_system` and `zemax_open_file` no longer record duplicate HighImpact operations merely for readback.
- `zemax_save_file` treats the Zemax file as the primary result; constraint-sidecar failure is an auxiliary warning after a successful lens save.
- Quick Focus and Scale Lens use strict documented criterion/unit vocabularies and propagate cancellation.

### Stage B — Sequential editing

- Surface setters preserve omitted-vs-explicit-clear semantics for material/comment/stop and fixed/variable solves.
- Surface solve/XDAT/PARM/aperture setters validate ranges, finite values, referenced surfaces, and enum vocabulary before mutation.
- Pure parameter reads use an explicitly ReadOnly execution command rather than accidentally creating HighImpact snapshots.
- Field, wavelength, system-aperture, vignetting, and common settings operations propagate cancellation.
- Named enums are required where numeric enum strings would otherwise create undocumented behavior.

### Stage C — structured read-only analysis

- Normalized ray inputs, field/wavelength/surface ranges, sampling, frequency, and named settings are validated before analysis execution.
- Fan/aberration/MTF/PSF/text parsers fail explicitly when required sections are missing or malformed instead of filling missing values with zero.
- `zemax_spot_diagram`, `zemax_rms_spot`, and `zemax_cardinal_points` use side-effect-free `IMeritFunctionEditor.GetOperandValue`; analysis tools are guarded against structural MFE mutation.
- Relative illumination, encircled energy, aperture throughput, GIA, and remaining structured analyses require real primary data and propagate cancellation.
- `zemax_geometric_image_analysis` is strongly typed and remains filesystem/configuration side-effect free; persistent analysis export belongs to Stage F.

### Stage D — configuration and glass catalog

- MCE operand creation validates named types before insertion, checks `ChangeType`, and rolls back failed post-insert setup.
- Current-configuration, operand deletion, and configuration-count changes check official mutation return values and read back resulting state/count.
- MCE cell readback preserves `Double`/`Integer`/`String` types and ConfigPickup source metadata.
- Fixed-value and ConfigPickup setter modes are mutually exclusive, type-checked, finite, range-checked, and use checked `SetSolveData`/`MakeSolveFixed` paths.
- Glass filtering validates finite/range/melt-frequency constraints before evaluation.
- All requested source catalogs must exist; filtering/export never silently falls back to only the correctly spelled subset.
- AGF numeric parse errors include catalog/line context rather than becoming plausible zero values.
- Export catalog names are confined to the Zemax `Glasscat` directory and use same-directory temporary files with final move/replace semantics.
- ZOS-independent simulation fixtures cover malformed AGF data, invalid filters, path escape, and overwrite/no-clobber behavior.

Stage D static review is complete. Actual OpticStudio MCE/catalog integration remains part of licensed-machine acceptance.

### Stage E — optimization

- Local Optimize accepts only explicit finite cycle choices; it no longer maps arbitrary values to a different cycle count or exposes an unbounded synchronous `Infinite` run.
- Global Search and Hammer treat `RunAndWaitWithTimeout` as a wait timeout rather than a stop signal: wall-clock timeout/cancellation explicitly cancels and drains the ZOS system tool before stable result readback/close.
- Hammer uses the official `AutomaticOptimization` and `TargetRunTimeM` settings instead of exposing ignored parameters.
- Custom LM cancellation propagates through finite-difference Jacobians, linear algebra/trial steps, and merit evaluation; cancellation restores the last accepted design and rethrows `OperationCanceledException`.
- Multistart preserves Job Cancelled semantics, propagates cancellation through variable/material discovery and every LM trial, and checkpoints through `CopySystem` so saving a checkpoint cannot rename the active optical system.
- Variable discovery and the optimization accessor use `IMCERow.GetOperandCell(configuration)` for MCE variables rather than treating a configuration number as a raw editor column index.
- Variable and merit data must be finite. A non-finite weighted MFE row fails custom optimization rather than being silently dropped from the objective.
- Constraint batches are validated/staged before commit; sidecars use strict finite parsing and atomic temporary-file replacement.
- `zemax_add_operand` validates before insertion, checks typed cell writes and `ChangeType`, and rolls back a failed insertion; cancellation is not wrapped as ordinary failure.
- `zemax_optimization_wizard` uses the current Wizard2 API and an entire-MFE `.MF` backup/restore transaction; unsupported wavelength selection is explicit.
- `zemax_forbes_merit_function` validates sampling/Radau/wavelength inputs, preserves zero field weights, checks MFE mutations/cell types, propagates cancellation, and restores the complete original MFE on failure or cancellation.
- `zemax_save_merit_function_file` writes through a temporary `.MF` with final no-clobber/replace semantics. `zemax_load_merit_function_file` backs up and restores the entire MFE if loading, validation, calculation, or cancellation fails.
- `zemax_get_merit_function` returns typed active parameter cells and fails on non-finite weighted data instead of sanitizing/ignoring failures into zero values.

Stage E static review is complete. Numerical convergence quality, ZOS optimization runtime behavior, and real checkpoint/MF interoperability remain part of licensed-machine acceptance.

### Stage F — POP, NSC, tolerancing, and exports

- `zemax_pop` is HighImpact rather than ReadOnly because it can export raw/BMP/ZBF files and temporarily change per-surface POP resampling state.
- POP validates beam/data/sampling/surface/field/wavelength/output inputs, uses typed `IAS_PhysicalOpticsPropagation`/DataGrid result access, polls the analysis lifecycle for cancellation, rejects non-finite grid data, and restores temporary `ResampleAfterRefraction` values in `finally`.
- POP raw-grid/BMP/ZBF outputs use explicit overwrite policy and same-directory temporary-file commits; a requested output that was not actually produced is an error.
- `zemax_get_nsc_detector` uses the official `GetDetectorDimensions(... out Rows, out Cols)` ordering and cross-checks rows × columns against detector size, fixing the prior nonsquare-detector row/column swap.
- NSC object pagination is strict, position/tilt values must be finite, and type-specific parameter reads preserve Integer/Double/String cell types instead of coercing them to one numeric representation.
- TDE reads preserve `IsParam1/2/3Used` and Nominal/Min/Max used flags; a used numeric bound must be finite and invalid pagination is explicit.
- Generic `zemax_export_analysis` uses a fixed supported-analysis allowlist, cancellable analysis lifecycle, strict BMP/TXT extension/output semantics, and atomic final file commits. BMP failure never silently creates a TXT fallback.
- `AnalysisBmpHelper` distinguishes “no renderable DataGrid” from invalid grid/filesystem errors, rejects non-finite pixels, supports cancellation, and writes only to fresh temporary paths.

Stage F static review is complete. POP physics, NSC detector behavior, version-specific analysis grid/text availability, and exported BMP/TXT/ZBF/raw-grid interoperability remain deferred to licensed-machine acceptance.

## 6. Release gate

A build is release-ready only when all of the following are true:

- GitHub Windows CI is green at the exact release-candidate commit;
- the live MCP 2026-07-28 verifier passes against a licensed OpticStudio installation;
- safety verification passes in the intended release mode(s);
- Host/Worker RPC version and manifest fingerprint agree in live health;
- no P0/P1 finding remains in every functional stage declared complete for release;
- the release ZIP/update signature and rollback checks pass;
- the tested OpticStudio version(s) are recorded in the release notes.

The current branch has completed static Stage A-F review as documented above, but **licensed OpticStudio acceptance has intentionally not yet been performed**.
