# AI engineering planning — v1.5.1 P1 development branch

This documentation covers **unmerged PR #40**, not a change to the signed
v1.5.1 release. The new tool is a read-only ZOS-API model inspection and
planning operation. It **does not execute** any proposed step.

## Tool: `zemax_task_plan`

```json
{"task":"clipping"}
```

Other supported intents: `imaging`, `straylight`, `energy`,
`optimize`, `tolerance`, and `safe-edit`.

Unlike `zemax_tool_catalog(task=...)`, which uses the static manifest,
`zemax_task_plan` reads the **currently connected** OpticStudio model through
the serialized Worker STA. It returns:

- `systemMode`: the *actual* Sequential or NonSequential system type.
- `systemFile`, LDE surface or NSC object count, fields and wavelengths
  when available.
- `modeCompatible`: whether the selected task is meaningful in this mode.
  An incompatible plan never silently converts the lens.
- Ordered steps with reason, availability in the selected Host profile,
  **applicability to the current sequential/NSC mode**, and a separate
  approval flag for non-read-only operations.
- `preflightWarnings`: unit/normalization risks, absent optical geometry,
  confirmation/snapshot needs, model mode and permission caveats.

Availability is calculated from the configured **profile and global read-only
setting**. A scoped credential can have stricter per-token access. AI clients
must compare steps against their actual `tools/list`; a plan is not a grant of
permission. `safe-edit`, `optimize`, and NSC detector tracing remain subject
to live ownership, authorization and user approval.

### Example: folded off-axis-mirror clipping

1. Call `zemax_tool_catalog(task="clipping")` without Worker startup for
   static discovery.
2. Call `zemax_task_plan(task="clipping")` once the correct lens is loaded.
3. Confirm the model is sequential, inspect target local LDE coordinate
   systems, pupil range, fields and wavelengths.
4. Use the suggested read-only ray trace, footprint and aperture tools,
   distinguish genuine vignette codes from ray-trace errors.
5. Form an evidence-backed report with the dominant blocking surface,
   sampled fractions and explicit unmodeled mechanical apertures.
6. Suggest an adjustment, **but never modify/save automatically** without
   confirming the target model and intended change.

For real radiometry, this remains a **geometric and scalar-ray approximation**;
see `docs/ENGINEERING_OPTICS.md`.

## One-call clipping report

For a sequential off-axis/parabolic mirror or folded spectrometer model:

```json
{
  "surfaces": [3, 4, 7, 9, 12],
  "hx": 0,
  "hy": 0,
  "wavelength": 1,
  "gridSize": 21,
  "maxPointsPerSurface": 48
}
```

Use `zemax_diagnose_clipping` as the first **read-only** clipping analysis.
It calls the existing `zemax_ray_footprint` and
`zemax_aperture_throughput` samplers, validates that clear/vignetted/error
counts are **identical for the same final surface and pupil grid**, and fails
closed on any disagreement. Output includes ranked vignette-code surfaces,
sampled geometric throughput, non-traceable ray count, locally defined
footprint/explicit circular aperture geometry, and reasons for uncertain
mechanical conclusions. Never infer that an unmodeled aperture is clear, or
that unmodeled structural clipping has been observed. This does not change
the loaded lens or move any surface.

Call `zemax_task_plan(task="clipping")` beforehand if there is uncertainty
about the current model mode or selected analysis tools. For models with
more than 24 sequential surfaces, select a bounded subset containing the
destination surface; the report may flag other reported blocking surfaces
that were not sampled.

## Worker and Tasks reliability

- The ZOS-API STA has a bounded pending queue (default 64) with FIFO order,
  a clear full-queue error, and immediate cancellation/removal of **queued**
  operations. A COM call already running on the STA cannot be forcibly
  interrupted inside the process; Host's Worker-generation hard-recovery
  remains responsible for a real COM hang.
- The Host writes an atomic, bounded, **metadata-only** Tasks journal under
  its current Windows user's LocalAppData/ZemaxMCP/task-journals directory,
  separated by listener port. It never saves raw optical tool outputs or
  Worker-provided diagnostic messages that might contain private lens paths.
- On Host restart, old in-flight Tasks are retained as owner-scoped
  `failed` entries with an explicit interruption reason. Old terminal
  Tasks remain visible only to their original owner. Completed results are
  marked expired rather than reconstructed or fabricated. Neither Job
  execution nor optical COM state is resumed from disk.
- Journal corruption/unavailability fails closed for persisted metadata;
  the live in-memory Task ledger remains authoritative if a disk write
  fails. Journal is **not encrypted**, and must remain on a private local
  user profile, not a shared/network-mounted directory. User identity and
  Task metadata may be sensitive even without result payloads.

## Observation and control lease semantics

A genuinely ReadOnly tool uses the same serialized Host gate but **does not
acquire or renew** another client's persistent write lease. The model is still
stateful: a read reflects the moment of execution and is not an atomic
multi-step design snapshot. A foreign read is rejected while an optical
background Job holds the system, preventing a misleading model read during a
long or non-cooperative COM operation. Lens mutations, optimization and
long-running Jobs retain exclusive owner/generation semantics.

Official Tasks capacity is reserved before a Job begins. If all active Task
slots are occupied, an explicit tool error is returned and no Job starts; a
Tasks-capable client is not silently handed a legacy Job ID. A surprise
registration failure after starting a Job returns a clear Job ID in an error
for manual follow-up.

## New diagnostics semantics

- **Check connection** (Overview): Host authentication, Worker, ZOS-API and
  license status only.
- **Test MCP tools** (Diagnostics): actual `tools/list`, a read-only
  `zemax_status` tool call and `server/discover` for official Tasks
  capabilities. This requires Worker access and is intentionally not a
  passive liveness probe.
- **Scoped /health**: returns connection/license status plus only Jobs/Task
  identifiers owned by the authenticated credential. It hides Worker PID,
  global lease holder, raw trace/result payloads and optical file paths.
  Scoped `/activity` stays a liveness-only response to avoid cross-owner
  event disclosure.
- **Busy health**: `workerBusy: true`, `statusFresh: false` and
  `lastKnownStatus` make it explicit that ZOS-API and license metadata
  may come from the **last successful GetStatus on this Worker generation**.
  Health does not queue a new status RPC behind an active foreground
  COM call or retained optical Job. A first-ever busy request without any
  verified status returns unknown values rather than inventing a disconnection.
  The Launcher labels the state "busy/last known" instead of "offline".
  A new Worker generation invalidates the cache; late status replies from
  retired generations are never accepted.
- **Worker tool result**: an explicit JSON `null` lookup is
  `isError: true`; Launcher validates returned Job IDs and terminal states
  before saying cancellation was accepted.

## Acceptance expectations

Windows CI must pass schema/security checks, WPF launcher render/smoke,
Private RPC fake-Worker two-token HTTP isolation (including owner-specific
Task metadata), and post-recovery liveness. Numerical live validation and
real frozen COM recovery remain separate licensed acceptance gates.
