# Post-1.5.0 engineering acceptance

Work continues **on the existing** `agent/official-tasks-adapter-20261008`
branch, fast-forwarded to the released 1.5.0 main commit before editing.
This is an unreleased draft, **not** a change to the signed v1.5.0 ZIP.

## Completed code paths / CI scope

| Area | New work | CI / fixture gate |
| --- | --- | --- |
| Non-cooperative Worker recovery | Fake Worker never answers `zemax_test_hang`; verify soft/hard deadline, generation replacement and a subsequent functional call | `VerifyNonCooperativeToolHardRecoveryAsync`; deterministic transport/process simulation, **not COM** |
| AI tool planning | `zemax_tool_catalog(task=...)` returns six ordered playbooks with available and unavailable steps for the current profile | Generated manifest, tool-schema checks, safety metadata |
| Launcher Job center | Bounded 25-item current/recent Job view, progress/queue/ID; confirmation and cooperative cancellation through existing MCP `zemax_job_cancel` | Launcher WPF build and offline layout check; actual same-owner cancellation requires live multi-client validation |
| Result conventions and coverage | Explicit scientific data, transport/domain-error and units rules; 136-tool coverage JSON inventory with *no invented live evidence* | `scripts/export-tool-coverage.ps1`; CI validates entire known catalog |
| NSC pixel data and energy budget | OpticStudio `GetDetectorData` native 1-based pixel API, 4096-cell bounded ROI and detector flux/hit totals; per-detector received/launched ratio only with an explicit denominator | Static ZOS-API compatibility build, bounded schema/safety checks; **licensed numeric acceptance still required** |

## Licensed validation for new optical readings

On a **dedicated** OpticStudio machine, use a known NSC sample with source rays
actually reaching detector object `4` (change the ID if needed). The
script opens a copied fixture, never the reference sample itself.

```powershell
./scripts/verify-live-functional.ps1 `
  -FixturePath "C:\ZemaxValidation\known-detector-scene.zos" `
  -AllowReplaceCurrentSystem -VerifyNsc `
  -NscEnergyDetectorObject 4 `
  -ReportPath ".\artifacts\live-nsc-data.json"
```

For a scientifically defensible normalized efficiency, independently establish
the source power **in the same OpticStudio native source units and same trace**,
then add `-NscLaunchedFlux <measured-or-defined-positive-value>`.
The report compares summary data, native ROI pixel data, and independently
requested energy-budget detector flux. These are not a radiometric
whole-instrument energy-conservation proof; detectors may see repeated rays.

The official 2024 R1 `INonSeqEditor` exposes
`GetDetectorData(int objectNumber, int pixel, int data, out double value)`.
Here `pixel=0` is a total summary, `pixel=-3` is hits, and
`pixel>=1` addresses a physical detector pixel.
Source: https://developer.synopsys.com/docs/zos-api-interface-2024-r1/interface_z_o_s_a_p_i_1_1_editors_1_1_n_c_e_1_1_i_non_seq_editor.xhtml

## Remaining release gates (do not claim PASS yet)

1. **Actual ZOS-API COM call frozen inside the dedicated STA.** CI can
   deterministically hang a fake Worker and enforce its hard recovery; that
   does not prove graceful/faulty COM behavior on an installed version.
   Run a controlled dedicated-host COM hang experiment with an isolated
   licensed instance and report before considering this gate closed.
2. Verify the new NSC readout on **OpticStudio 2024 R1.03** with known flux,
   nonzero hit counts, ROI orientation/reference, and a specified physical
   source-power denominator. Then test other explicitly supported API versions.
3. Verify Launcher cancellation for local/shared and scoped owner identities.
   It must fail closed on another client's Job; Launcher is **not** given an
   administrator override. A process-global forced kill is never part of
   normal Job-center cancellation.
4. Only after real sample evidence, consider the new tools production ready.
   Keep this PR Draft and publish a new signed version via normal release
   validation rather than modifying the v1.5.0 artifacts.
