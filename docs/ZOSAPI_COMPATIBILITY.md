# ZOS-API cross-version compatibility

OpticStudio's ZOS-API is not a frozen ABI. It has existed since OpticStudio 15, but API functionality is added over time. The public Zemax MCP package therefore must not assume that a Worker compiled against a recent ZOS-API can safely run against an older installation.

## Compatibility model

The release ZIP does not redistribute `ZOSAPI.dll`, `ZOSAPI_Interfaces.dll`, or `ZOSAPI_NetHelper.dll`. The net48 Worker is compiled against one installed OpticStudio **build baseline**, while the user-selected OpticStudio installation supplies the proprietary assemblies at runtime.

A release package now records the compile-time `OpticStudio.exe` Product/File version plus ZOS-API Product/File/Assembly versions in `ZOSAPI_BUILD_INFO.txt`. `OpticStudio.exe` is the primary product-release anchor because managed assembly identities can remain comparatively stable across product releases. `BootstrapProgram` loads the selected installation's ZOS-API assemblies and compares the product/API baseline **before** loading `ServerApplication`, which contains compile-time ZOS-API type references.

- Runtime product/API equal to or newer than the build baseline: startup is allowed. This establishes the expected binary/API direction, but licensed functional acceptance is still required.
- Runtime product/API older than the build baseline: Worker startup is rejected with an explicit compatibility error. This prevents a newer-compiled Worker from failing later with `MissingMethodException`, `TypeLoadException`, or a partially executed operation.
- Developer build with no baseline marker: startup remains possible, but the Worker reports that cross-version compatibility was not preflighted.

Version parsing normalizes both legacy Zemax forms such as `21.3.2` and Ansys release forms such as `2024 R2.01` / `2026 R1` onto the same comparable scale.

## Release build rule

To support a range of OpticStudio releases, compile the Worker against the **oldest release that the package claims to support**:

```powershell
$env:ZEMAX_API_BASELINE_ROOT = 'C:\Program Files\Zemax OpticStudio 2021\OpticStudio'
./scripts/publish-windows.ps1
```

`ZEMAX_ROOT` remains a developer/single-version fallback, but a package produced that way has that installation as its minimum runtime API baseline. The publish script warns about this explicitly.

The Worker is explicitly built as `x64`. This is important for older `ZOSAPI_NetHelper.dll` releases, where an AnyCPU/32-bit process could query the wrong Windows registry view and fail to locate the 64-bit OpticStudio installation.

## Compile compatibility matrix

Before claiming support for a release family, compile the complete Worker against the actual ZOS-API assemblies from each installed version. This requires the DLLs but does **not** start OpticStudio or consume a license:

```powershell
./scripts/verify-zosapi-compatibility.ps1 -ZemaxRoots @(
  'C:\Program Files\Zemax OpticStudio 2021\OpticStudio',
  'C:\Program Files\Zemax OpticStudio 2023\OpticStudio',
  'C:\Program Files\ANSYS Inc\v242\OpticStudio',
  'C:\Program Files\ANSYS Inc\v261\OpticStudio'
)
```

A compile failure is treated as a real compatibility failure: some source member/type is newer than that baseline and must either be replaced by an older equivalent, late-bound behind an explicit capability check, or documented as unavailable for that release.

## Current compatibility assessment

The following is the repository/API assessment before licensed multi-version acceptance. **Compatibility target is not the same as verified support.** A row becomes verified only after the complete Worker compiles against that version's real ZOS-API assemblies and the licensed live acceptance passes.

| OpticStudio family | Current status | Notes |
| --- | --- | --- |
| 2026 | Static reference reviewed | Current ZOS-API documentation used for exact signatures; licensed runtime acceptance still pending. |
| 2024 | Compatibility target | No known architectural blocker; real 2024 DLL compile matrix and live smoke test still required. |
| 2023 | Compatibility target | Same as 2024. Explicit x64 Worker avoids the legacy NetHelper bitness/registry issue. |
| 2021 R3 / 21.3+ | Compatibility target | `.ZOS` exists from 21.3 onward, but safety code no longer depends on it. Enhanced Ray Aiming was still experimental; later-only settings are capability-detected. |
| 2021 R1/R2 / 21.1-21.2 | Compatibility target, not verified | `.ZOS` does not exist. Safety snapshots and unsaved multistart checkpoints use `.ZMX`. Ray-aiming settings added during the 21.x/22.1 Enhanced Ray Aiming transition are late-bound and return `null` plus `UnsupportedSettings` when absent. The complete Worker still must compile against the actual 21.1/21.2 ZOS-API DLLs before support can be advertised. |

Important reviewed APIs are older than 2021: the sequential Off-Axis Conic Freeform appeared in OpticStudio 20.2, POP was added to ZOS-API in 20.3, `ISEQOptimizationWizard2` was already the recommended wizard interface before 2021, and `IMCERow.GetOperandCell(configuration)` appears in Zemax examples from 2019. These specific Stage E/F changes therefore do not by themselves force a 2023/2024/2026 minimum.

One concrete post-2021 surface was found during this audit: Enhanced Ray Aiming became a formal feature in OpticStudio 22.1, while Advanced Convergence, Fallback Search, and Number-of-Steps evolved through the 2021 feature experiments. `zemax_get_ray_aiming_settings` therefore no longer directly binds those optional members at compile time. It capability-detects `UseEnhancedRayAiming`, `UseAdvancedConvergence`, `UseFallBackSearchDuringCacheSetup`, and `NumStepsCacheSetup`; unavailable members are represented as `null` and listed explicitly instead of being fabricated as `false`/`0`.

## File-format compatibility

`.ZOS` was introduced in OpticStudio 21.3. For release safety operations the project uses `.ZMX` as the cross-version snapshot format because modern OpticStudio continues to support ZMX while 21.1/21.2 cannot read ZOS files.

- Automatic pre-change safety snapshots: `.zmx`.
- Unsaved multistart checkpoint default: `.zmx`.
- Already-saved multistart systems retain the current design's `.zmx` or `.zos` extension.
- User-requested open/save operations still honor the user's explicit file format and the capabilities of the selected OpticStudio version.

## What still requires a real machine

Compile compatibility proves that the source only references members available in a given ZOS-API assembly set. It does not prove behavior. The final release matrix must still run `verify-live-mcp.ps1` against licensed installations and include representative operations for System/Sequential editing, MCE, local/global optimization, POP, NSC detector access, tolerancing, and file export.
