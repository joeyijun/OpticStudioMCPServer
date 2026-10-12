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

This is **not** a completed design/optimization acceptance. Live verification
of the correction and the bounded focus-search scenario remains pending
deployment.

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
