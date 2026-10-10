# Engineering optical diagnostics (post-v1.5.0 draft)

This document describes **unreleased** engineering additions on PR #40. They
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
  "startSurface": 1,
  "relativeSourceSpectralWeights": [0.8, 0.2],
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
- Optional `relativeSourceSpectralWeights` aligns with the selected wavelength
  list and is normalized without overflow. Returned per-field spectral
  aggregates remain **dimensionless geometric survival and ray-intensity
  proxies**, not detector-collected watts or absolute throughput; no source
  spectrum is silently assumed. A zero weight is allowed but all-zero,
  negative, non-finite and wrong-length inputs fail.
- Optional `assumedSurfaceRta` plus `followReflectedBranch` permits a
  **separately labeled USER-assumed, passive grey R/T/A ledger** over the
  selected window. Supply one [R,T,A] triple and reflection/transmission
  branch flag per LDE surface. It validates R+T+A<=1, tracks diverted
  other-branch power separately from absorption, and refuses nonfinite,
  negative or non-passive coefficients. **These are NOT OpticStudio coating
  properties** and must not be multiplied into ZOS real-ray intensity (which
  may already include the coating), nor treated as a trace-calibrated watt
  result. The independent ledger can audit known coating datasheet values
  before a later physical power-balance acceptance.
- Calls are bounded to <=6 fields, <=6 wavelengths, <=24 consecutive
  surfaces per call, and <=150000 surface/ray samples. For a long LDE use
  absolute `startSurface` and `finalSurface`, with **one shared boundary
  surface** between windows: 1..24, 24..47, 47..70.
  `finalSurface: 0` means the image plane. Each surface reports cumulative
  entrance-pupil survival, not window-normalized power; the first row in a
  window cannot report its transfer from the previous surface.
  The shared boundary permits first-new-surface transfer in the next window.
  Never multiply cumulative clear-pupil fractions from separate windows.

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

- Optional `includeGlobalCoordinates: true` reads OpticStudio's official
  `LDE.GetGlobalMatrix(surface)` vertex transform and maps each actually
  traced **local X/Y/Z ray intercept** to the global 3D frame. Local Z
  includes curved-surface sag; this is NOT the incorrect flat tangent-plane
  approximation. Outputs include `globalCoordinates.rotation` and
  `origin`, global centroid and XYZ min/max of ALL surviving samples,
  and up to `maxPointsPerSurface` global 3D sample points. If the matrix
  fails, the tool fails instead of silently returning a guessed transform.
  Coordinate axes and lens units follow the active model, not arbitrary CAD.
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

### User-declared mechanical outlines (new)

`zemax_ray_footprint` can compare **surviving ray intercepts** against one
user-measured physical outline **in the local coordinate frame of that LDE surface**.
For example, a 6 mm × 4 mm opening centered at the surface origin:

```json
{
  "surfaces": [4],
  "mechanicalRectangle": [-3, -2, 3, 2],
  "maxPointsPerSurface": 24
}
```

For nonrectangular polished flats, use `mechanicalPolygon` instead:
`[[x1,y1], [x2,y2], ...]` with 3..64 non-self-crossing local XY vertices.
When querying several LDE surfaces, supply `mechanicalSurface` identifying
which target matches the supplied outline. A polygon is user evidence:
the tool does not change Zemax apertures, infer CAD-to-LDE transforms or
assume optical `SemiDiameter` equals the mechanical clear boundary.
Returned `userMechanicalBoundary.minimumSignedClearance` is positive
inside and negative for sampled surviving intercepts outside. Its
`outsideFractionOfSurvivors` denominator excludes already-vignetted and
failed rays; **it must never be reported as the full-source cutting loss**.

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
- For **large arrays**, request `includeTilePlan: true` and
  `tilePlanPage: 0` in a dimensions-only call. The `nativeTilePlan`
  returns at most 64 native row-major tiles per page (each at most 64×64
  pixels); request further pages, then call `zemax_get_nsc_detector`
  separately for each returned `startRow`, `startColumn`, `rowCount`,
  `columnCount`. The client may assemble/export those tiles, preserving native
  row/column orientation. No unbounded single RPC or implicit file write.
- With `includePixels: true`, setting `heatmapBins: 2..16` returns
  `roiMeanHeatmap` as a small matrix of per-bin **mean native pixel values**,
  not detector power, physical image orientation or flux integral.
- ROI is bounded to 4096 native pixels per call; rectangular detector
  pitch derives from actual X/Y half-width and X/Y pixel counts, not a
  guessed default. Pixel #1 is at local (-X,-Y), columns increase +X and
  rows +Y. This ordering is not a screenshot raster ordering. Detector
  Surface/Volume/Color/Polar geometries are not assumed to share that
  pixel pitch; values that cannot be derived safely stay `null`.
- `roiFluxIntegral` integrates dataType 0 directly, and dataType 1 over
  physical pixel area **only** if detector geometry supports it.
- For a requested ROI, `roiPixelMin`, `roiPixelMax`,
  `roiPixelMean`, `roiNonzeroPixelCount`, `roiPeakRow` and
  `roiPeakColumn` describe the native **per-pixel** values and location
  (0-based detector coordinates). They are intended for spot diagnostics
  and heatmap annotation, **not** instrument throughput. Peak intensity
  is never substituted for `roiFluxIntegral`.
- If detector data overflow finite arithmetic or its explicit
  normalization produces NaN/Infinity, the request fails rather than
  publishing a misleading flux/efficiency. For DetectorVolume with
  `dataType:1`, a launched-flux ratio describes **absorption**, not the
  collection efficiency inferred from incident-flux pixels.
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

### Model design difference check

`zemax_system_summary` accepts optional `baselineSummaryJson` containing
a previous **successful** `zemax_system_summary` JSON result (up to 96 KB).
It then compares the current live model metadata to the previous snapshot
**without opening or switching files**. The additional
`baselineComparison.differences` covers units, mode, aperture, sampled
surfaces, fields and wavelengths. Numerical JSON `10` vs `10.0`
is treated as the same number; omitted LDE rows are marked **unknown**, not
silently classified as unchanged, added or removed. This comparison does NOT
establish which design is optically better: confirm any changes with PSF/MTF,
real ray footprints, throughput, source/detector flux or tolerance analysis.

### Job/Task incremental diagnostics (Host and Launcher)

`GET /mcp/jobs-delta?cursor=<previous opaque cursor>` is a
**non-COM-blocking** status endpoint that reads the Host's already validated
Worker status cache, buffered Worker progress events and (for scoped clients
only) the metadata-only Task ledger. Authentication is identical to
`/mcp/health`. In scoped-credential mode it returns only Job IDs recorded
as owned by that authenticated credential **and that Worker generation**.
Shared/local mode can see Job diagnostics but never receives Official Task IDs.

The response is `{cursor, changed, snapshot}`. A changed response includes
`snapshot.workerGeneration`, `workerBusy`, `statusAvailable`,
`jobs` (Job ID, tool name, state, fractional progress, queue and message)
and `tasks` (safe owner-specific Task metadata). An unchanged response has
`snapshot: null`, preventing redundant Job/Task data transfer.
Cursors are process-local, HMAC-protected, owner-specific and reset across
Host/Worker generations; neither cross-client event counts nor raw optical
tool results are embedded. An active COM operation does not stall this
endpoint because no Worker status RPC is issued.

The Launcher tries this endpoint at one-second cadence and falls back
to its existing five-second `/health` polling on older Hosts (HTTP 404).
The delta is a cached/progress observation, **not** proof that hardware
execution progressed, that a cancellation completed, or that the underlying
OpticStudio model is numerically correct.

## Launcher monitoring and smoke checks

The Launcher separates **Overview**, **Tasks**, **Settings**, and **Diagnostics**.
Overview contains service state and common actions. Installation, LAN sharing,
and remote setup live in Settings; connection details and local logs are in
Diagnostics. The window uses Mica with an accessibility/OS fallback, without
a material selector.
Jobs and, for scoped bearer owners, result-free official Task metadata are
shown with state, generation, duration, a failure/message field and
owner visibility. The displayed owner can be a **control-lease** holder and
must not be confused with the creator of a Job. Result/cancel operations
always pass through the existing scoped MCP methods; a different credential
cannot cancel or inspect another client's Tasks.

- **Test connection:** first checks Host authentication, Worker availability,
  ZOS-API loading/connection and reported license status, then performs real stateless `tools/list`, real read-only
  `zemax_status` tool result, and modern `server/discover` Tasks capability discovery.
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

### Privileged NSC scalar detector CSV export

`zemax_export_nsc_detector_csv` writes explicitly selected **native NSC pixel
values** as `row,column,value` CSV in the existing directory on the **OpticStudio
computer**, not on the AI client's computer. This is a **HighImpact file write**,
unavailable in `basic-viewing` and blocked by global read-only mode. It never
runs a trace, clears detectors, or changes the optical model.

Specify `objectNumber`, `csvPath`, positive `rowCount`/`columnCount`,
0-based `startRow`/`startColumn`, `dataType` and optional `overwrite`
(default false). One export is bounded to **262144 pixels**; subdivide bigger
arrays into successive ROI files and join their native row/column indices.
File writes use a sibling temporary file and atomic commit so cancellation,
missing or nonfinite detector data cannot publish partial results. The
returned summary includes native pixel count, min/max/sum and a SHA-256
digest of the resulting CSV. Color/polar detectors are not supported by this
generic scalar API. Data type 1 is *absorbed flux* for DetectorVolume, not
irradiance, and absolute orientation is not inferred from the display.

### Standalone AI preflight and result explanation tools

`zemax_validate_model` accepts `purpose`:
`model-review`, `imaging`, `clipping`, `energy`, `straylight`.
It reuses the bounded serialized `zemax_system_summary` and returns
purpose-specific blocking metadata findings plus concrete next checks.
`noIdentifiedMetadataBlockers` is **not** physical validation, not a lease,
not a permission to edit, and not a guarantee that ray tracing will succeed.

`zemax_explain_result` accepts an explicit completed `toolName` and
`resultJson` (up to 128 KiB). Supported inputs are
`zemax_energy_budget`, `zemax_ray_footprint`,
`zemax_get_nsc_detector`, `zemax_nsc_energy_budget` and
`zemax_system_summary`. This deterministic whitelist extracts evidence-backed
numerical metrics, explains native units/denominators and suggests next tools.
It **never** opens a model, performs ray tracing, ranks one design as optically
superior, or invents absorption/throughput from missing information.
