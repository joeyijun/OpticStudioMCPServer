# 1.5.2 acceptance

The deployed `41948e4` build was exercised on OpticStudio 2024 R1.03 over
authenticated HTTPS on 2026-10-13. This is representative workflow acceptance,
**not** a claim that all 152 tools or supported OpticStudio versions were tested.

## Passed on the deployed build

- Empty/default MFE read; replacing its mandatory blank row through the
  optimization wizard; successful bounded one-cycle optimization.
- Ordinary synchronous/Job calls, automatically negotiated official Tasks,
  explicit synchronous override, native result equality, indeterminate running
  progress, cooperative PSF cancellation and blocked competing STA reads.
- 100 consecutive official Tasks returned actual successful spot calculations.
  Worker terminal metadata stayed at 30; oldest Task metadata was evicted and
  the latest result remained available.
- Sample-copy system/settings/surface/solve inspection, PSF, cardinal points,
  ray footprints, aperture/energy/clipping workflows and guarded surface edits.
- Cooke tolerance operand inspection and a small sensitivity/Monte Carlo run.
- NSC diode scene trace, same-trace detector snapshot, configured source metadata,
  native detector ROI/heatmap/tile-plan readout and per-detector energy inspection.
- The active user system was saved to a unique backup and reopened after each
  test group; optical data readback was compared. Original files and Samples
  were not overwritten; no normal Worker was forcibly terminated.

## Findings and code fixes requiring another deployment

- Host expanded every Job-list request to the obsolete `193` record window.
  Host/Worker now share the 30 history + 64 queued + 1 running policy; only
  scoped lists overfetch for owner filtering. Shared lists preserve the actual
  requested limit.
- Five English-only text parsers failed on genuine Chinese reports. A narrow
  heading normalizer now passes both sanitized numeric fixture tests and the
  full exported reports (field curvature, focal shift, lateral color, relative
  illumination, OPD fan). Fullwidth punctuation, number/unit boundaries and
  duplicated short-wavelength labels are covered; numeric rows are unchanged.
- Deployed `d86d949` subsequently passed 65 representative checks, including
  Job lists and all six localized curve/Fan analyses. Seidel was the only
  remaining failed tool. The original SeidelCoefficients and PupilAberrationFan
  reports were exported by the native installed OpticStudio and supplied for
  local parser verification. All seven full reports and sanitized fixtures now
  pass numeric/metadata assertions; Seidel totals and wavefront summary are
  independently checked against those reported values. The new Seidel mapping
  passed the final candidate's actual end-to-end tool call before release.
- Export supports `SeidelCoefficients` and `PupilAberrationFan` so their original
  text can be collected without an arbitrary remote-file-read API.

The semi-diameter-margin tool correctly reports that the installed API lacks
this capability; no synthetic zero result was accepted as a measurement. A
failed test using `fieldSampling=3` was a harness mistake (valid values 1/5/9),
not a product defect. SeidelDiagram is distinct from SeidelCoefficients and
does not support TXT export on this installed version.

## Release gate

The final `72c48b6` candidate was deployed to both computers on 2026-10-13.
All 66 representative main-flow checks passed (including Seidel, the other
localized analyses, Job lists, controlled edits, NSC detector/energy inspection
and tolerance analysis). Another 28 focused checks passed for background Jobs,
negotiated official Tasks, actual results, cancellation/drain, STA interleaving
protection, MFE wizard and single-cycle optimization. The compiled Launcher Job
result query also succeeded against the deployed endpoint, without HTTP 400.
The original system was independently verified after restoration.

The README screenshot is the user-supplied 2026-10-12 dashboard image. These
documentation-only acceptance changes do not alter the validated optical code.
Merge and publish only after exact-head hosted CI is green; rebuild the signed
release package from the exact main commit. This does not certify every one of
the 152 tools or arbitrary inputs/OpticStudio versions.
Real non-cooperative COM hangs, other OpticStudio versions, arbitrary CAD and
absolute radiometric energy conservation are outside the completed evidence.
