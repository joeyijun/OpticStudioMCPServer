# OpticStudio MCP — remaining work and acceptance gates (2026-10-10)

This is an **engineering backlog**, not an unverified percentage estimate.
The Windows simulation, Host/Private RPC, desktop, policy and updater
workflow is mandatory for every change. A passing synthetic CI run
is **not** evidence of physically correct OpticStudio numbers.

## Eight engineering work packages

| ID | Work package | What is present | Concrete remaining acceptance gate |
|---|---|---|---|
| E1 | Same-trace source → detector energy provenance | NSC trace can clear **all** detectors and snapshot up to 16 native flux/hit records inside that same successful session; optional NCE configured source-power metadata is separate from a user-provided denominator | Independently validate actual launched-ray power/source inclusion, capture per-ray branches and detector revisits from the same trace and prove category disjointness; do **not** infer loss as (P_{source}-\sum P_{detector}) |
| E2 | Polarized coating / material / CAD attribution | `zemax_coating_rta` reads native per-interface S/P R/T/A by explicit AOI/wavelength; `ZrdPathEnergyCore` checks parent/branch intensity consistency and `zemax_audit_native_zrd` adds a bounded, Caution-gated official ZRD reader for explicit local files; still no unverified loss attribution | Implement a bounded, approved native ZRD ingestion adapter, version-check parent semantics, map actual AOI/polarization/material interactions and classify loss only where native evidence is sufficient |
| E3 | Native detector geometry and power calibration | Scalar flux, ROI, tiles/CSV, source-weighted relative spectral proxy, explicit user-calibrated local pixel-axis signs and peak position; native handedness is never guessed | Validate pixel axis and handedness across source/detector types; check incoherent flux, irradiance area integration, source units and overlapped detectors on live models |
| E4 | Mechanical CAD and clipping | Local/global footprints, finite stop-plane intersection, consecutive LDE segments, up to 12 polygonal stops with first-blocker counting, and bounded opaque triangle mesh first hits | Triangulated opaque ray/mesh *surface* first-hit is now bounded to 256 global XYZ triangles and consecutive LDE chords; watertight solid/B-rep validation, arbitrary curved/multi-bounce NSC paths and measured source-denominator remain outstanding |
| E5 | Launcher architecture and UX | Separated client configurator, network diagnostics, TaskCenter view model, endpoint+credential-bound cursor, status presentation and validated Host launch arguments with environment-only credentials | Migrate remaining service **process lifecycle**, status dashboard, settings/navigation and error presentation to independently tested MVVM services/commands without breaking XAML or tray flows |
| E6 | Durable Job/Task lifecycle and multi-client isolation | Scoped ownership, bounded history, metadata Task journal, Worker hard recovery, activity and jobs deltas | Verify cancellation/hung COM/reconnect/restart state transitions on real Windows/OpticStudio, preserve result-expiry semantics, add remaining scoped incremental state as needed |
| E7 | Secure LAN/remote deployment | Opt-in local PFX HTTPS, scoped bearer, remote HTTP warnings, proxy HTTPS awareness, tested startup-plan argument validation and secrets delivered only through environment variables | Validate remote clients with trusted CA/SAN hostname, certificate rollover, TLS proxy→Host hop and distinct credential isolation under adverse connections |
| E8 | AI engineering workflows and useful reports | System summary, purpose preflight, clipping, sampled radiometric proxies and deterministic explanation of same-trace detector, ZRD path and opaque CAD evidence | Cross-tool trace-identity verification, a unified evidence-linked model comparison/report, safe tool sequencing, and correct unit provenance across native ZRD and measured detector power |

## Separate final live acceptance stage

**L1 — Licensed OpticStudio / real hardware.** Use an actual model suite:
sequential folded/off-axis objective with coatings and mechanical stops;
NSC source → mirrors/absorbers → detectors with splitting/scattering;
a polarized test case; multiple source wavelengths; reproducible independent
detector native-data reads. Run at least two supported ZOS-API releases
where available and verify clean cancellation/Worker restart.

Report numeric assertions with tolerances, model versions, input source units,
exact lens/save state and detector orientations. CI's fake Worker
does not satisfy L1. Keep the PR Draft until both software and physical gates
have been reviewed.

## Conservation/non-double-counting invariants

1. `sameTraceEnergy`: readings must follow a successful ray trace in the
   *same serialized ZOS session*, after clearing all detector buffers.
   A queued background Job has **no** flux observations yet.
2. NCE source `Power` is an optical model configuration parameter, not
   independently measured launched power. Never silently use it as a verified
   conservation denominator.
3. Native detector incident flux may already include physical loss,
   multiple visits and ray splitting. Detector flux contributions are
   non-additive unless disjointness is independently proven.
4. Native interface S/P R/T/A from a different per-surface AOI query
   is **not** a new independent attenuation factor to multiply into
   an already traced native detector flux.
5. A sampled CAD ray may be charged to **only the earliest blocker**;
   downstream blocked counts must never charge it twice.
6. ZRD parent-to-child intensity differences are not automatically
   “bulk absorption” or “coating loss”; branch completeness, ray statuses,
   hit face, polarization, material and native-version semantics must be
   validated before attributing physical categories.
7. Distinguish **unknown/unverified** from **zero** in every API and report.

## Completion semantics

A work package is *done* only when its functional implementation,
unit/integration tests, security boundaries, failure/recovery tests and
appropriate optics numeric acceptance all have evidence. This document
deliberately does not assign a completion percentage to partially implemented
physics.

## 2026-10-10 current implementation audit (software-only)

This continuation has added:
- E2: `zemax_audit_native_zrd` with official native ZRD reading, explicit absolute local file path, 128 MiB / 1024 ray / 8192 segment limits, file digest, strict parent-reference checking, and **no** fabricated physical loss categories; this is **not** a same-trace provenance handshake
- E3: `NscPixelCalibration` plus user-declared signed detector-local peak conversion; absent empirical signs means **unknown** local orientation, replacing the old unverified lower-left claim
- E4: `CadTriangleMeshAudit` with user-supplied nonplanar *collection* of <=256 opaque triangles, conditional first ray hit per physical part, bounded normalized pupil and null/ambiguous classifications
- E5/E7: `HostLaunchPlan` separates WPF from validated startup argument construction and keeps bearer/PFX secrets in child-process environment variables; a live trusted-LAN connection is still not tested
- E8: `zemax_explain_result` now differentiates native ZRD ray graph intensity differences and first-hit opaque CAD counts from **proven** watts/absorptance

The presence of a feature does **not** close its E-package until
appropriate physics-version acceptance is performed. E1/E2 energy
conservation and E6 real COM cancellation are still explicitly open.
