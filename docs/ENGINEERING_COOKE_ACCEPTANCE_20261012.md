# Cooke engineering acceptance: preflight defect and correction

The deployed `176b328` build was tested through the authenticated HTTPS MCP
endpoint using the installed official **Cooke 40 degree field** sample.

## Live result

- The user's dirty Standalone optical system was saved to a unique backup,
  without overwriting the original lens.
- Surface, field and wavelength DTOs were unchanged by the backup.
- The official sample was opened, then saved to a unique working copy before
  attempting any engineering edit.
- Solve preflight failed: `MarginalRayAngle_Angle` does not exist on the native
  solve object. No guarded design edit was attempted.
- The user's backup was reopened; the original model DTOs matched.

That initial run was **not** a completed design/optimization acceptance.
After deployment of `4844bcc`, the bounded focus-search scenario below passed.

## Completed live follow-up: 4844bcc

Both the local running Launcher and the remote Worker reported this build.
The complete final run passed **56 assertions** across **76 tool calls**.
An additional read-only MTF tradeoff-recognition check passed.

The official sample has a Variable image-gap thickness and a MarginalRayAngle
radius solve on surface 6. Fixed and Variable thicknesses are editable in
this experiment; derived solves must not be overwritten. The test preserved
these definitions throughout the focus search.

Only a uniquely named working copy was edited. A 0.2 mm image-gap defocus
was introduced, then five image-gap candidates were evaluated. Every edit
used a new baseline, a dry-run preview, a guarded batch, an independent surface
read and workflow verification. The selected candidate returned to the
original nominal image gap, 42.2077801 mm.

| Metric | Deliberately defocused | Selected candidate |
| --- | ---: | ---: |
| Axial RMS spot radius | 12.0348 µm | 5.0808 µm |
| 20° field RMS spot radius | 22.7287 µm | 11.9523 µm |
| Axial FFT MTF, 10 cycles/mm | 0.83518 | 0.93519 |

Axial RMS decreased by 57.78%. This is recovery from imposed defocus, **not**
an improvement beyond the original official design. The numerical spot criteria
passed, but the stricter claim that every field/component MTF improved was
correctly rejected: 14° tangential MTF fell from 0.93013 to 0.70761 at
10 cycles/mm. Five of six selected MTF components improved; the comparison
returned `criteriaPassed=false` and identified the failing component.

Additional live boundaries verified:

- A consumed baseline was rejected with zero applied edits, unchanged guarded
  model state and no additional safety snapshot.
- Native angle solve setting/readback and Fixed → Variable thickness-solve
  roundtrip succeeded; all original solve definitions were retained.
- All 75 bounded real rays were clear, with zero API error/vignette codes.
- The result was saved only to the test copy, then the user's unique backup
  was reopened and model DTO equality was verified.

This acceptance covers a bounded sequential imaging exercise, not all tools,
NSC/tolerancing, every OpticStudio version, a complete lens fingerprint or
production optical-design validation. Model files and snapshots were retained
for diagnosis rather than deleted while an optical system could reference them.

## Correction

Get/set solve readback now shares a strongly typed reader using
`ISolveData._S_*` interfaces. Angles do not invent a pupil-zone parameter;
position distance and reference surface are separate; material offsets retain
both refractive-index and Abbe offsets. Existing `Height` angle aliases remain
for result compatibility, with an explicit `Angle` field added.

Setting solves now uses `IEditorCell.CreateSolveType` / `SetSolveData`,
checks supported types and the returned `SolveStatus`, rather than calling
nonexistent dynamic `MakeSolvePickup` / `MakeSolveMarginalRayAngle` methods.
Pickup columns use native `SurfaceColumn` enum values, not an invented ordinal
mapping. Material substitution stores a catalog only; a material-name change
must be a separate explicit edit.

Official contracts:

- [ISolveData](https://developer.synopsys.com/docs/zos-api-interface-2025-r2-sp04/interface_z_o_s_a_p_i_1_1_editors_1_1_i_solve_data.xhtml)
- [ISolveMarginalRayAngle](https://developer.ansys.com/docs/zos-api-interface-2025-r1/interface_z_o_s_a_p_i_1_1_editors_1_1_i_solve_marginal_ray_angle.xhtml)

## Local regression

Run with a locally held, licensed ZOS-API reference set:

```powershell
dotnet run --project tests/ZemaxMCP.NativeSolveTests -c Release -p:ZEMAX_ROOT=C:\Path\To\ZOSAPI-build-reference
```

Fourteen readback assertions cover the native solve interfaces, including the
Cooke angle regression, pickup, ray height/angle, edge thickness, position,
F-number, center of curvature, substitution, offsets and type-only solves.
These tests exercise interface-backed fakes, not COM or OpticStudio execution.
The Worker must also compile against the real native references. Vendor DLLs
are never committed or included in the distributed runtime package.
