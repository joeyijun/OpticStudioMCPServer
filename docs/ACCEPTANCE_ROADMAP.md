# OpticStudio MCP — remaining work and acceptance gates (2026-10-10)

This is an **engineering backlog**, not an unverified percentage estimate.
The Windows simulation, Host/Private RPC, desktop, policy and updater
workflow is mandatory for every change. A passing synthetic CI run
is **not** evidence of physically correct OpticStudio numbers.

## Eight engineering work packages

| ID | Work package | What is present | Concrete remaining acceptance gate |
|---|---|---|---|
| E1 | Same-trace source → detector energy provenance | NSC trace can clear **all** detectors and snapshot up to 16 native flux/hit records inside that same successful session; optional NCE configured source-power metadata is separate from a user-provided denominator | Independently validate actual launched-ray power/source inclusion, capture per-ray branches and detector revisits from the same trace and prove category disjointness; do **not** infer loss as (P_{source}-\sum P_{detector}) |
| E2 | Polarized coating / material / CAD attribution | `zemax_coating_rta` reads native per-interface S/P R/T/A by explicit AOI/wavelength; `ZrdPathEnergyCore` checks parent/branch intensity consistency but leaves differences unexplained | Implement a bounded, approved native ZRD ingestion adapter, version-check parent semantics, map actual AOI/polarization/material interactions and classify loss only where native evidence is sufficient |
| E3 | Native detector geometry and power calibration | Scalar flux, ROI, tiles/CSV, source-weighted relative detector spectral proxy | Validate pixel axis and handedness across source/detector types; check incoherent flux, irradiance area integration, source units and overlapped detectors on live models |
| E4 | Mechanical CAD and clipping | Local/global footprints, finite stop-plane intersection, consecutive LDE segments, up to 12 polygonal stops with first-blocker counting | Nonplanar solid/B-rep or triangulated CAD geometry, ray/solid intersections across actual multi-bounce paths; expose normalized source-denominator only when original ray population is known |
| E5 | Launcher architecture and UX | Separated client configurator, network diagnostics, task projection/TaskCenter view model, endpoint+credential-bound delta cursor, pure status presentation | Migrate service lifecycle, status dashboard, settings, navigation and error presentation to independently tested MVVM services/commands without breaking XAML or tray flows |
| E6 | Durable Job/Task lifecycle and multi-client isolation | Scoped ownership, bounded history, metadata Task journal, Worker hard recovery, activity and jobs deltas | Verify cancellation/hung COM/reconnect/restart state transitions on real Windows/OpticStudio, preserve result-expiry semantics, add remaining scoped incremental state as needed |
| E7 | Secure LAN/remote deployment | Opt-in local PFX HTTPS, scoped bearer, warning for remote HTTP, correct client-path HTTPS proxy status | Validate remote clients with trusted CA/SAN hostname, certificate rollover, TLS proxy→Host hop and distinct credential isolation under adverse connections |
| E8 | AI engineering workflows and useful reports | System summary, purpose preflight, clipping, source/target ray intensity proxies and deterministic result explanation | Evidence-linked model comparison/report, safe sequencing of tool calls, result-unit provenance and clear separation of unknown loss categories vs measured flux |

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
