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

### Global XYZ planar mechanical outlines

For a measured clear aperture that is described directly in OpticStudio's
**global XYZ lens-unit coordinate system**, set `mechanicalGlobalPolygon`
with 3–64 coplanar [x,y,z] vertices. This is mutually exclusive with
`mechanicalRectangle` / `mechanicalPolygon`. Specify
`mechanicalSurface` for multi-surface analysis, and select an explicit
`mechanicalPlaneTolerance` (default **0.01 lens units**). The tool uses
actual traced local X/Y/Z and the official LDE `GetGlobalMatrix` to
classify only *surviving rays whose intercepts lie within that distance of
the CAD polygon plane*. Output reports `planeMatchedRays`,
`planeUnmatchedRays`, `outsideRays`, `minimumSignedEdgeClearance`
and `outsideFractionOfPlaneMatched`. For a curved optical surface or a
physically separate mechanical stop, a ray may be far from the CAD plane;
those rays are **unknown** and are never labeled cut. Noncoplanar,
self-intersecting or invalid CAD polygons are rejected, not flattened.

This feature does **not** solve arbitrary solid-model intersections or trace
a ray to a separate CAD plane. A noncoincident 3D edge needs a
ray–plane / ray–solid intersection at the correct optical propagation path,
which remains an independent engineering feature.

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
`snapshot.workerGeneration`, `ownerScoped`, `workerBusy`, `statusAvailable`,
`jobs` (Job ID, tool name, state, fractional progress, queue and message)
and `tasks` (safe owner-specific Task metadata). A just-registered,
owner-authenticated Job with no Worker status yet is reported with explicit
`state: Unknown`; its state and progress are NOT guessed. Confirm later via
`zemax_job_status` or the next valid progress event. An unchanged response has
`snapshot: null`, preventing redundant Job/Task data transfer.
Cursors are process-local, HMAC-protected, owner-specific and reset across
Host/Worker generations. For scoped credentials, `workerBusy` and
`statusAvailable` in this endpoint refer to **visible owned Job states**,
not global Worker load/freshness, so another client's execution cannot move
this owner's cursor. Neither cross-client event counts nor raw optical
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

### Explicit detector spectral response (discrete source-weighted proxy)

`zemax_energy_budget` now optionally accepts
`relativeDetectorSpectralResponse`, a finite relative detector
efficiency in [0,1] for EACH selected wavelength, in the same order
as explicit `relativeSourceSpectralWeights`. It returns BOTH the unchanged
source-weighted `weightedRayIntensityProxy` and the additional
`responseWeightedRayIntensityProxy` and
`sourceWeightedDetectorRelativeResponse`. The response-weighted value
is `sum(source[i]*detectorResponse[i]*rayProxy[i])/sum(source[i])`,
**without renormalizing away detector loss**. An efficiency of 0.5
everywhere halves the proxy. These are sparse user-supplied spectral
bins, NOT measured detector watts, spatial ROI coupling, continuous
spectral integration or independently conserved input-output energy.

### Real single-surface coating R/T/A from installed ZOS-API

The read-only `zemax_coating_rta` tool uses the documented
`ILDERow.GetCoatingPerformanceData()` /
`ICoatingPerformanceData.GetCoatingPerformance(AOI, wavelen, direction)`
API. Supply 1–24 existing sequential LDE `surfaces`, 1–6
`anglesDegrees` (0–89.9 degrees), 1–6 existing wavelength indices
(default primary), and `direction` (`inward`/`outward`).
Calls are bounded to 120 total surface/wavelength/angle samples.
Actual ZOS-API S/P reflectance, transmittance and absorptance are read
independently, alongside an unpolarized arithmetic mean and
`residualS`/`residualP` = 1 – R – T – A, without clamping
or hiding energy-accounting residuals. The interface is version
checked at runtime: on an older installation without these methods,
the tool reports a clear failure rather than invented zeroes.

This is a **single-interface** coating performance calculation for
the explicitly supplied angle, wavelength and travel direction.
It does not automatically determine the angle of every incident ray,
the chosen optical branch, multiple bounces, bulk loss or detector
coupling. **Do not** multiply these values into
`zemax_energy_budget` ray intensities that may already include
the coating loss. Real licensed OpticStudio numerical acceptance and
cross-version validation are pending.

Official API references: 
https://developer.synopsys.com/docs/zemax-opticstudio-zos-api-2026-r1/reference/interface_z_o_s_a_p_i_1_1_editors_1_1_l_d_e_1_1_i_coating_performance_data.md

### Finite 3D ray segment vs independent planar mechanical stop

`zemax_ray_footprint` accepts
`mechanicalGlobalPolygon:[[x,y,z],...]` and
`globalMechanicalMode:"preceding-segment"` with
`mechanicalSurface>=2`. This mode traces the **same normalized-pupil
rays** to the chosen LDE surface **and the immediately preceding LDE
surface**, independently transforms their actual local XYZ/sag into
global coordinates with `LDE.GetGlobalMatrix`, and intersects the
finite STRAIGHT segment connecting each surviving pair with the
user-supplied global planar aperture. No infinite-line extrapolation
outside the segment is permitted. It reports counts of plane
intersections falling inside/outside the allowed aperture, no
intersection, coplanar ambiguity, degenerate segments, and signed edge
clearance. `OutsideFractionOfPlaneIntersections` uses only rays
that reach the plane; `OutsideFractionOfValidSegments` uses surviving,
nondegenerate, nonambiguous endpoint pairs. Rays already rejected
by the sequential optical model are excluded; neither value is a
source-normalized optical throughput or measured detector power.

The stop polygon is an **allowed clear opening**: an intersection
outside is potential extra CAD clipping if everything outside the
polygon is physically opaque. The stop must lie between the two
physical neighboring surfaces; a different LDE location, multiple
intermediate optical interactions, or a solid-body CAD model still
needs an explicit multi-segment/solid intersection analysis.
`globalMechanicalMode:"surface"` retains the original strict
plane-coincidence assessment. The analysis makes NO model edits.

The segment assessment also returns the exact
`mostCriticalIntersection` global XYZ at the minimum signed edge
clearance. With `maxPointsPerSurface>0`, the first 1–128 actual
stop-plane intersections can be returned in `sampleIntersections`
alongside an explicit `samplesTruncated` flag. These positions
belong to the **user-specified mechanical plane**, not the final
optical image plane.

### Same-trace NSC detector/source snapshot: measured vs inferred evidence

`zemax_run_nsc_ray_trace` accepts optional `snapshotDetectorObjects`
(1..16 unique native scalar NSC detector IDs) and optional
`declaredLaunchedFlux` (strictly positive user-supplied same-trace,
same-source-set native flux). A snapshot REQUIRES
`clearDetectors:true, detectorObject:0`: all detectors must be cleared
immediately before tracing to exclude accumulated previous-run values.
Immediately after **this same** successful trace, in the same serialized
ZOS session before releasing it, the tool reads `GetDetectorData(id,0,0)`
and `GetDetectorData(id,-3,0)`. This makes each detector reading
time/provenance consistent with the tool's trace, unlike a later
standalone detector query. In background mode the ledger is returned
only in the finished Job result; the initial Queued response has no
detector samples.

The `sameTraceEnergy` ledger states that launched source flux is
**user-declared**, not independently measured by ZOS; computes **only
individual detector** native flux / declared source flux ratios; and
marks `additiveSourceToDetectorBalanceValid:false`,
`unassignedEnergy:null`, and independent coating/bulk/mechanical
losses **not directly measured**. Multiple NSC detectors may register
the *same ray*, so their summed flux is NOT source-conserved. A native
coating S/P interface R/T/A sample from sequential LDE or a separate
geometry/clipping proxy must **not** be multiplied or subtracted from
an NSC detector flux that already includes those interactions.
Full per-interaction energy conservation requires separately verifiable
same-trace ray path/absorption provenance (e.g., validated ZRD), which
is NOT produced by this trace tool. This bounded design deliberately
avoids fabricating a complete optical energy ledger from incomplete data.

### Multi-stop CAD path (first blocker, no repeated geometric losses)

`zemax_ray_footprint` additionally accepts arrays
`cadStopGlobalPolygons` (1..12 separate planar clear openings, global XYZ)
and `cadStopAfterSurfaces` (one LDE surface ID BEFORE each stop),
plus an explicit **strictly consecutive increasing** `surfaces` array
covering every physical segment under analysis. It uses the same
normalized pupil and selected field/wavelength for ALL LDE surfaces,
transforms real local XYZ hits through each LDE GetGlobalMatrix and
tests only the finite chords between adjacent surfaces. Traced rays
blocked or invalid at an endpoint are `uncertainRays`. Other rays
are attributed to the FIRST CAD opening they would fail, sorted by
actual crossing location even for two stops within one LDE interval.
`multiStopCadPath.stops[i].firstBlockedRays` counts each ray **once**,
not once per downstream CAD stop. `firstBlockedRays + uncertainRays +
fullyCheckedUnblockedRays = sampledPupilRays`.

This remains a conditional geometric *proxy* on surviving sequential
rays, not an input-to-detector power balance. Do not add these losses
to ZOS-native ray intensity or native NSC detector flux, both of which
can already include physical aperture interactions. All chosen stops
are user-supplied opaque plane plates with a clear polygonal opening;
it does not intersect arbitrary solids or curved CAD.

### Multi-segment geometric continuity

When separate LDE target traces are stitched into a path, the
**shared global XYZ vertex** of each pair of adjacent segments is
checked to a bounded numerical tolerance. A discontinuity produces
`multiStopCadPath.discontinuousRayPaths` and contributes to
`uncertainRays`; it cannot yield an invented first obstruction
at a downstream CAD stop. This protects ray identity across
coordinate breaks and trace inconsistencies.

### Configured NSC source-power readback (NOT automatically source efficiency)

With `snapshotDetectorObjects` on
`zemax_run_nsc_ray_trace`, optional
`includeConfiguredSourcePower:true` reads up to 128 NCE
`IObjectSources.Power`, `NumberOfAnalysisRays`, and
`WaveNumber` values inside the same ZOS session after that trace.
`sameTraceEnergy.configuredSources` and
`configuredSourcePowerSum` expose model settings separately from the
native **post-trace detector readings**.

A source's configured Power is not an independently measured number of
watts or verified launched-ray power. Emitter visibility, source
selection, inactive sources, filters and special source types matter;
the configured sum MUST NOT silently become the efficiency denominator.
Detector `fractionOfDeclaredSource` therefore remains NULL unless
`declaredLaunchedFlux` is explicitly supplied by the caller and
verified against the actual source set. The source sum does not
reclassify missing power as coating absorption, bulk absorption or CAD
clipping.
