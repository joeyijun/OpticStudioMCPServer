# Engineering optical diagnostics (post-v1.5.0 draft)

This document describes **unreleased** engineering additions on PR #38. They
are read-only interfaces (except for the explicitly existing NSC tracing tool).
Windows CI proves registration/safety/build. Numerical claims and OpticStudio
version compatibility require the later dedicated **licensed** acceptance gate.

## Sequential: `zemax_energy_budget`

Example MCP `tools/call` arguments:

```json
{
  "fields": [[0, 0], [0, 0.5]],
  "wavelengths": [1, 2],
  "finalSurface": 12,
  "gridSize": 15
}
```

- A deterministic, uniform Cartesian sample inside the **normalized circular
  pupil** is traced via official `SingleRayNormUnpol` once per LDE surface,
  field and wavelength. `gridSize` is a square grid before clipping to a circle.
- Returns the **cumulative geometric clear fraction**, **vignetted rays**,
  **ray-trace error rays**, **ray intensity per input-pupil sample**, and
  effective **same-survivor-ray intensity transfer** between adjacent LDE
  surfaces. `vignetteCodeAtThisSurface` counts rays reporting the surface in
  the vignette code; never reclassify ray-trace errors as clipping.
- `MIRROR` material identifies a possible reflective path; this **does not**
  provide isolated coating reflectance or absorption. Ray intensity in
  unpolarized tracing is not a calibrated radiometric watt, source spectral
  power, photon flux, polarization Jones result, or scattering loss.
  To obtain true spectral/photometric throughput and detector efficiency,
  supply an actual source spectrum, physical surface/coating data, detector
  response and a trace-consistent launched flux; NSC is appropriate for
  detector/stray-light work.
- Calls are bounded to <=6 fields, <=6 wavelengths, <=24 consecutive
  surfaces starting from surface 1, and <=150000 surface/ray samples.

## Sequential: `zemax_ray_footprint`

Example:

```json
{
  "surfaces": [4, 6, 9, 12],
  "hx": 0,
  "hy": 0.5,
  "wavelength": 1,
  "gridSize": 21,
  "maxPointsPerSurface": 48
}
```

- Every output X/Y intercept, minimum/maximum envelope, clear-ray centroid,
  RMS, and explicit circular aperture center/radii are in the **local
  coordinate frame of the named LDE surface**, NOT one global 3D coordinate
  system. A Coordinate Break can change that frame.
- Only real explicit circular aperture/obscuration data are described as
  physical aperture limits. SemiDiameter is returned as a **reference**
  drawing aperture, not a proof of actual ray termination.
- Reports geometrically clear vs vignetted vs failed rays, the surface code
  associated with the earliest blocking signature and minimum outer circular
  aperture clearance for clear sampled rays when applicable. A zero
  vignette does not prove mechanical clearance not modeled in Zemax.
- A requested point limit affects only **returned plotted samples**; all
  sampled rays still contribute to the numerical envelope and clipping count.

## NSC: `zemax_get_nsc_detector` and `zemax_nsc_energy_budget`

Example:

```json
{
  "objectNumber": 4,
  "includePixels": true,
  "dataType": 0,
  "startRow": 10,
  "startColumn": 20,
  "rowCount": 24,
  "columnCount": 48,
  "launchedFlux": 1.0
}
```

- `dataType:0`: detector incoherent incident flux per pixel (native
  source-power units); `dataType:1`: surface/rectangle flux per area,
  but **volume detector absorbed flux**. Sum and physical integral are
  reported separately. Pixel values are NOT normalized to display maximum.
- ROI is bounded to 4096 native pixels per call; rectangular detector
  pitch derives from actual X/Y half-width and X/Y pixel counts, not a
  guessed default. Pixel #1 is at local (-X,-Y), columns increase +X and
  rows +Y. This ordering is not a screenshot raster ordering. Detector
  Surface/Volume/Color/Polar geometries are not assumed to share that
  pixel pitch; values that cannot be derived safely stay `null`.
- `roiFluxIntegral` integrates dataType 0 directly, and dataType 1 over
  physical pixel area **only** if detector geometry supports it.
- `totalIncidentFlux` is read separately from the detector's official
  summary slot; `roiFractionOfDetectorFlux` compares ROI to this total.
  `roiFractionOfLaunchedFlux` and
  `totalDetectorFractionOfLaunchedFlux` require an **explicit positive
  `launchedFlux` in identical source units and from the same ray trace**.
- `rayHits` are detector interactions, **not** a unique-hit-ray count.
  Repeated passes and NSC ray splitting mean `missedRayCount` cannot be
  obtained by subtracting hits from launched rays: it remains `null`.
  Several detectors can overlap and must not be summed to infer a complete
  loss budget. Untraced detector flux is `null`, not fabricated zero.

## AI planning

Run `zemax_tool_catalog` with one of these task keys:

| Task | Suggested sequence |
| --- | --- |
| `clipping` | System/surfaces → ray diagnostics → Ray Footprint → aperture throughput |
| `imaging` | System/fields → RMS spot → PSF → MTF |
| `straylight` | NSC objects → trace → detector data → NSC energy budget |
| `energy` | Fields/wavelengths → sequential energy budget / NSC detector budget |
| `optimize` | Snapshot/variables → merit function → optimization → verify |
| `tolerance` | Tolerance table → tolerancing → sensitivity interpretation |
| `safe-edit` | Readback → pre-change snapshot → edit → readback → snapshot diff → intentional save |

The catalog lists operations hidden by the selected profile separately.
These are planning hints, **not** permission grants or an automatic mutating
macro.

## Launcher monitoring and smoke checks

The Launcher now offers a separate **Tasks** page in addition to Overview.
Jobs and, for scoped bearer owners, result-free official Task metadata are
shown with state, generation, duration, a failure/message field and
owner visibility. The displayed owner can be a **control-lease** holder and
must not be confused with the creator of a Job. Result/cancel operations
always pass through the existing scoped MCP methods; a different credential
cannot cancel or inspect another client's Tasks.

- **Check connection:** Host authentication, Worker availability, ZOS-API
  loading/connection and reported license status.
- **Test MCP tools:** real stateless `tools/list`, real read-only
  `zemax_status` tool result, and `initialize` Tasks capability negotiation.
  Does **not** start a destructive or long optical operation.
- **Task result:** supply the exact Task ID in the Tasks page. Retrieval uses
  owner-authorized `tasks/get` and returns a terminal result if available.
  A Job ID is not a Task ID.

## Deferred licensed tests

After feature work is complete, execute on a dedicated OpticStudio machine:

```powershell
./scripts/verify-live-functional.ps1 `
  -FixturePath "C:\Validation\sequential.zmx" `
  -AllowReplaceCurrentSystem -VerifyEngineeringOptics `
  -BudgetFinalSurface 12

./scripts/verify-live-functional.ps1 `
  -FixturePath "C:\Validation\nsc.zos" `
  -AllowReplaceCurrentSystem -VerifyNsc `
  -NscEnergyDetectorObject 4 -NscLaunchedFlux 1.0
```

Only use a **known source power** for `-NscLaunchedFlux`; the number 1.0
above is an example, not a universal normalization assumption. The scripts
copy fixtures before opening. CI checks and schema validation cannot prove
physical accuracy of these numerical results.
