# Optical tool result conventions (post-1.5.0)

This is a **backward-compatible contract**, not a claim that all 141 tools now
share a new JSON envelope. Worker tool results remain the existing JSON text
inside MCP `CallToolResult.content`; do not silently wrap or rename historical
payloads. New result fields and new tools should follow these rules.

## Common interpretation

1. `success` describes the requested optical operation, not successful HTTP
   transport. `success: false` maps to MCP `isError: true`. A transport or
   serialization error is not a valid zero measurement.
2. Physical quantities must have explicit unit semantics. Lens units,
   normalized field/pupil coordinates, wavelength index (1-based), detector
   source-flux units and flux-per-area are **different**. Do not infer a unit
   solely from a property name.
3. NaN / Infinity in calculated optical data are not fabricated as finite
   zero. For legitimate optical infinity, use JSON `null` with a paired
   `Finite/PositiveInfinity/NegativeInfinity` state, as in cardinal points.
4. `isError` and `success` are not interchangeable with Task state: a
   completed Task may contain a domain error (`isError: true`). A failed Task
   means result transport / Worker generation itself failed.
5. Missing / expired Job result and cancellation must be reported explicitly,
   never reconstructed from a Job ID or presented as completed optical output.
6. Structured outputs should record acquisition context (object/surface,
   field/wavelength, detector type, trace or sampling options), size bounds,
   clear orientation and optional loss/normalization assumptions.

## NSC power and detector data

- `zemax_get_nsc_detector` returns dimensions and summary flux/hit count;
  `includePixels: true` reads a **bounded native 0-based row/column ROI**
  through the official **1-based pixel** API. `dataType: 0` means
  per-pixel incident flux; `dataType: 1` means flux per detector area for
  rectangle/surface detectors but absorbed flux for volume detectors. Color
  and polar detectors require dedicated API calls rather than generic flux
  decoding. These values are not interchangeable or guaranteed to be a
  visually upright heatmap.
- Dimensions-only inspection stays valid when a newly opened detector has not been traced: unavailable summary power/hits are **null**, never invented as zero or used as an efficiency measurement.
- One response contains at most **4096 pixels**. Larger images require
  multiple explicit, non-overlapping ROIs; never truncate silently.
- `zemax_nsc_energy_budget` reports incident flux for **individual** detector
  objects. Only an explicitly supplied positive `launchedFlux` in the
  **same source power units and optical trace** enables a
  `fractionOfLaunchedFlux` calculation. Multiple detectors may see the
  same rays; the results are **not additive** and do not prove an overall
  system efficiency.
- A detector's measured flux is not the same as incident source flux,
  particularly with absorption, splitting, scattering, coherent fields,
  overlapping detectors, mixed wavelengths or polarization. Document the
  source denominator and whether it is simulated or measured.

## Test evidence

The new `scripts/export-tool-coverage.ps1` enumerates the explicit public
tool inventory and records coverage conservatively. A green CI build proves
contract/simulation checks only. To claim licensed execution, supply the
original JSON acceptance report and map each passing *scenario* to its
tool list. A passing workflow is **not** blanket approval for every tool
nor a validated optical numerical answer.

Required high-value ZOS fixtures for new features:
1. NSC diode scene: detector object ID, pixel ROI, total incident flux,
   ray hits and a known source normalization; independently compare detector
   totals with `zemax_nsc_energy_budget`.
2. Sequential clipping scene: verify `zemax_aperture_throughput` against
   manual vignette and trace-error counts. This is geometric pupil efficiency,
   not a complete spectral radiometric budget.
3. Tasks fault injection: fake Worker stall and timeout/relaunch in CI; real
   non-cooperative OpticStudio COM hang is a separate licensed machine gate.

**Before shipping** a new optical number as verified, archive the exact
version, fixture copy, parameter values, expected numerical tolerance,
observed optical result, license/API version and pass/fail record.
