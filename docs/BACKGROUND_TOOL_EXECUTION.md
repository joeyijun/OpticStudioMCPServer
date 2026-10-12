# Background tools and official MCP Tasks

The installed tool contract now explicitly supports background execution for
**23 tools**. This is a capability, not a claim that every call is long or that
multiple optical operations run concurrently.

## Selection and compatibility

| Client / selection | Result |
| --- | --- |
| Ordinary client, newly supported tool, background option omitted | Original synchronous tool result |
| Ordinary client, `runInBackground=true` | Managed Job ID; poll `zemax_job_status` |
| MCP 2026-07-28 client advertising the Tasks extension, server Tasks enabled, newly supported tool, option omitted | Automatically start a Job and return an official Task handle |
| Tasks-capable client, explicit `runInBackground=false` | Synchronous result, not a Task |

The five original native-Job tools keep their existing behavior and defaults.
Four accept `runInBackground` and default it to true; multistart always starts
a Job. A client that does not negotiate official Tasks still receives the Job
handle, rather than an unsupported Task response.

The common `BackgroundToolPolicy` is shared by the Host and Worker. It is not
inferred from a tool name/domain and does not override tool authorization.
The catalog reports `backgroundExecution` as `none`, `native-job` or
`shared-runner`; this capability is included in the manifest fingerprint.

## Supported operations

Existing native Jobs:

- `zemax_pop`
- `zemax_run_nsc_ray_trace`
- `zemax_run_tolerancing`
- `zemax_global_search`
- `zemax_multistart_optimize`

New shared-runner capabilities:

- Optimization: `zemax_optimize`, `zemax_constrained_optimize`, `zemax_hammer`.
- MTF: `zemax_fft_mtf`, `zemax_fft_mtf_vs_field`,
  `zemax_geometric_mtf`, `zemax_geometric_mtf_vs_field`.
- PSF / image / energy: `zemax_fft_psf`, `zemax_huygens_psf`,
  `zemax_diffraction_encircled_energy`, `zemax_geometric_encircled_energy`,
  `zemax_geometric_image_analysis`.
- Spot: `zemax_spot_diagram`, `zemax_rms_spot`.
- Bounded diagnostics: `zemax_ray_trace_diagnostics`,
  `zemax_energy_budget`, `zemax_ray_footprint`, `zemax_diagnose_clipping`.

Quick metadata and surface edits are not automatically promoted to Jobs.

## Worker execution and safeguards

The shared runner adds optional schema controls without changing each native
tool's method signature. It strips only those reviewed controls, then uses
the existing strict reflection binder; unknown native arguments still fail.
The method executes exactly once through the existing Job manager and STA.
Native tool validation, safety snapshots, rollback and cancellation semantics
remain in the native implementation.

`backgroundTimeoutSeconds` defaults to 300 and accepts integers from 1 to
7200. It limits **running time**, not time spent waiting in the Job queue.
Expiry requests cancellation and uses the existing bounded drain/hard Worker
recovery. It does not replace a tool-specific native timeout.

Not every native COM/analysis call can stop immediately. If it cannot
cooperatively drain, hard recovery replaces the Worker generation; it does
not pretend that cancellation succeeded while COM is still running.

Read-only background analysis also holds the optical model until terminal
state. Foreground STA operations, including an owner's new analysis, are
rejected during an active Job so they cannot block the cancellation/status
path. Metadata, Job queries and cancellation remain available. A newly
acquired read-only owner is temporary and releases after its Jobs finish;
an already established persistent writer owner is not silently revoked.

The accepted response is not the final optical result. A native
`success:false` becomes a failed Job, not an invented successful completion.
Final results retain the existing engineering observation/evidence metadata.
Queued Job handles are not recorded as completed analysis evidence.

Running tools without reliable intermediate progress report an indeterminate
state and elapsed time. Completion sets 100%; no synthetic 10%/50% estimates
are manufactured.

There is still one Worker and one STA. Background tasks improve responsiveness,
polling, cancellation and visibility; they do not provide parallel lens models.

## Verification

Local tests cover all 23 declared capabilities, schema defaults and bounds,
strict argument cleaning/binding, exactly-once execution, real result retention,
native failure, timeout/drain, honest progress and temporary read-only leases.
HTTP → Host → Fake Worker E2E covers ordinary sync/Job fallback, shared-token
Job cancellation, automatic official Tasks, exclusion of foreground STA work,
Task polling/results and explicit synchronous override.

These are local simulation/Fake Worker tests, plus real-reference Worker
compilation. Licensed acceptance of the newly deployed implementation remains
a separate step; no customer lens was opened or optimized for these tests.
