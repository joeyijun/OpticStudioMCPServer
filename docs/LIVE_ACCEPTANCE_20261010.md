# Live acceptance — 2026-10-10

## Target-version gate: blocked, not passed

Local branch under test: `agent/p1-workflows-reliability-20261009`,
implementation commit `d93930a`. It declares 146 tools.

The existing remote endpoint reports only 138 full-expert tools, Host and
Worker assembly versions `1.5.0.0`, RPC v3, and matching remote manifest
fingerprints `7c3f5ed2aff210082fe61054adeb293a69407bcfba7a0952589a96076f25dd6f`.
The intended 146-tool version gate failed. Assembly versions alone do not
identify the deployed Git commit; the discovery count proves it is not the
intended full-expert tool contract.

## Existing-service baseline: passed

Authenticated modern MCP 2026-07-28 discovery and catalogue agreed on 138
tools. ZOS-API was loaded from OpticStudio 2024 R1.03, connected, with a
valid PremiumEdition API license. All 17 read-only calls in
`scripts/verify-live-mcp.ps1` passed, including full system/surface readback,
configuration, metadata, first-order data, units, fields, wavelengths,
polarization, aperture, ray aiming, environment and material settings.

This baseline used `-ExpectedFullExpertToolCount 138` explicitly, only after
recording the target-version failure. It does not waive the default 146-tool
release gate or accept the new optical tools.

## Safety and next gate

No file was opened, saved or replaced; no lens edits, ray trace, background
job, cancellation or Worker termination were requested. Credentials were
read from the existing current-user protected Launcher settings, never
printed or persisted in this report.

Deploy the new acceptance package on both computers and restart the services.
Then rerun with the default count of 146 and compare the actual manifest
fingerprint. Only after this gate should dedicated temporary model copies be
used for engineering-optics, detector/coating/ZRD and recovery acceptance.
The physically meaningful numeric and real COM-hang gates remain open.

## After deployment: target-version gate passed

The deployed endpoint now exposes exactly 146 full-expert tools, RPC v3,
and identical Host/Worker fingerprints
`1e24ef3bc53006270a08529f952dd9ccab4bd5e4d0401280fd31d5489d0272dc`.
All 17 baseline read-only calls passed again. The local AI computer runs the
Launcher from the new acceptance package; it does not run a local optical
Host on port 8000, which is consistent with using the remote endpoint.

Licensed runtime: OpticStudio 2024 R1.03, valid PremiumEdition API license.
Detailed raw results are retained locally in outputs/
`live-engineering-20261010-final.json` (not uploaded to GitHub).

### Passed engineering checks

- Official sequential `Double Gauss 28 degree field.zmx` was opened and saved
  to a GUID-named remote temporary copy before optical checks. System summary,
  unchanged-baseline comparison, imaging preflight and clipping plan returned
  structured results.
- An 11x11 circular pupil grid contains 81 rays. Energy-budget category totals,
  footprint, aperture throughput and clipping diagnosis agreed on these ray
  counts; the on-axis image case had 81 clear rays, zero vignettes and zero
  trace errors. Global coordinates came from native GetGlobalMatrix.
- Native AR coating R/T/A returned four S/P samples (surfaces 1 and 2, 0 and
  30 degrees, wavelength 0.4861 micrometers). Normal-incidence S/P coefficients
  were equal; R approximately 0.0121483312, T approximately 0.9878516688,
  A=0. Reported closure residuals were at floating-point roundoff scale.
- A user-declared image-plane rectangle [-1,-1,1,1] contained all 81 clear
  samples; the displaced [1,1,2,2] rectangle contained none. This tests bounded
  supplied geometry, not a measured mechanical assembly or solid CAD proof.
- Four malformed inputs were rejected, including a relative ZRD path;
  read-only analysis did not change the copy's path, surface count or save state.
- Official NSC `Ray splitting/Beam splitter.ZMX`, also saved to a unique
  temporary copy, traced with splitting and polarization enabled. Same-trace
  snapshots for detectors 4..7 matched subsequent native detector readback and
  the energy-budget tool within `1e-9 * max(1,abs(flux))`. Each 2x2 ROI had a
  consistent raw pixel sum. Detector fluxes were approximately 0.4509231201,
  0.4509231201, 0.02247372194 and 0.07247372194 in native units.
- Configured source power was 1, but was correctly NOT treated as independently
  measured launched power. Additive closure stayed false; unassigned energy and
  automatic source-normalized fractions stayed null.
- Original saved `C:\Users\admin\Desktop\LENS.zos` was reopened and its clean
  save state confirmed after every run. Samples/original lens were not saved
  over; temporary models and safety snapshots are retained for diagnosis.

### Open issue: native ZRD positive-path acceptance

The explicitly selected archived `Short course/Archive/sc_stray2.ZRD` returned
`Native ZRD contains no auditable ray records` when read from a pure NSC system.
Opening its similarly named ZMX instead reports Sequential mode and is rejected
by the tool's NSC gate. This does not establish whether the problem is the
archived fixture, mixed-mode support, reader filter defaults or the adapter.
No root cause or successful ZRD ingestion is claimed. The normal service
remained usable and the original file was restored.

The current reader call sequence was compared with the official
[2024 R1 ZRD example](https://developer.ansys.com/docs/zos-api-interface-2024-r1/_c_sharp_standalone_05__read__z_r_d__file_8cs-example.xhtml).
A newly generated bounded, pure-NSC ZRD plus native viewer/reference readback
is still needed before accepting this feature. A safe rejection is not a
successful data-ingestion test.

No real COM hang/kill recovery, trusted TLS/SAN, second OpticStudio version,
watertight CAD model, absolute source-power calibration or complete 146-tool
numeric suite was exercised. These results do not close all E1–E8/L1 gates.

The repeatable acceptance script supports an explicit ClientInstanceId so a
single verifier can resume its OWN lease without claiming another client's
identity. Subsequent attempts with a new random verifier instance were correctly
denied by lease isolation. The test lease may remain until its configured idle
expiration; the script does not disconnect the user's optical service just to
force lease release.
