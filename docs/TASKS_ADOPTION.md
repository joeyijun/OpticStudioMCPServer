# Official MCP Tasks adoption assessment

Status (1.5.0 / RC7): **experimental, default ON**, as requested. The Launcher exposes **Run configuration → Enable official Tasks (experimental)** and persists the preference. Existing installations without the setting adopt ON; explicitly saved OFF remains OFF. Change it on the OpticStudio computer, then Stop / Start when idle; editing this preference never automatically terminates an optical operation. Command-line Hosts can disable it using `--enable-official-tasks false`. Clients must still opt in per request; other clients retain ordinary `tools/call` plus `zemax_job_status/list/cancel`. Licensed NSC and tolerancing Task completion and cooperative Global Search cancellation passed on OpticStudio 2024 R1.03. Non-cooperative COM hard recovery and a broader licensed version matrix remain outstanding; these are not inferred from cooperative cancellation or fake-Worker tests.

## Protocol / SDK facts

- The host uses `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` **2.1.0**, with stateless HTTP and MCP protocol **2026-07-28** support.
- The official v2 Tasks extension is delivered separately through `ModelContextProtocol.Extensions.Tasks`. A compatible client opts in with the `io.modelcontextprotocol/tasks` per-request extension. Only then may the server return a task handle (`resultType: "task"`); otherwise an ordinary `CallToolResult` must be returned.
- The official extension offers `tasks/get`, `tasks/update`, and `tasks/cancel`. Tasks on a `2025-11-25` connection cannot use the incompatible v1 experimental protocol.
- The SDK's `WithTasks(new InMemoryMcpTaskStore())` automatically wraps tool invocations as background tasks. It is **not a safe one-line upgrade** for this server: long-running ZOS-API tools already queue a Worker Job and immediately return a Job ID, which would make the outer Task terminal before the actual optical operation finishes. Auto-wrapping every tool would also change semantics of currently synchronous high-impact commands.
- For stateless HTTP the Tasks store must be shared across requests. For production, the SDK recommends a custom `IMcpTaskStore` supporting strong consistency, terminal-state idempotency and bounded durable retention.

Sources:
- https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tasks/tasks.md
- https://modelcontextprotocol.github.io/ext-tasks/specification/2026-07-28/tasks.html

## Required design before enabling

**Single underlying job.** Offer Tasks only for approved long-running commands (initially NSC tracing, tolerancing, POP, global search and multistart). Create the *one* underlying `McpJobManager` Job on the Worker; associate it with a Host `taskId`, a `jobId`, the authenticated client ID and the Worker generation. Do not run a second, detached copy of the operation. The Task must not report completion merely because the Worker returned `{jobId}`.

**Ownership-first authorization.** Every `tasks/get`, `tasks/update`, and `tasks/cancel` must enforce the same token-owned identity as scoped `zemax_job_status/list/cancel` before reading details or effecting a cancellation. A revoked credential cannot poll or cancel, nor can a second token guess another task handle. Legacy, single-token mode retains its pre-existing scope. Task discovery must not leak task IDs.

**State mapping.** Map Worker `Queued/Running/Cancelling` to `working`, `Completed` to `completed` with the final *actual* `CallToolResult`, `Cancelled` to `cancelled`, and `Failed` to `failed` only for protocol-level failures; ordinary tool-domain errors should be completed with `isError:true`. Preserve the actual result payload and any explicit expired-result signal. Worker generation death or hard recovery must terminally fail affected Tasks rather than leave them endlessly working.

**Cancel + timeout.** `tasks/cancel` acknowledges intent and calls the existing owner-authorized cancellation path. The Worker already has a separate cancellation grace / hard-recovery deadline; reuse it, do not invent a competing COM cancellation mechanism. Cancellation is eventually consistent and must not overwrite an already completed result.

**Storage and retention.** A singleton Host Task store is the minimum for stateless HTTP. Explicitly cap queue, terminal Task history, results, and progress; use the authenticated owner as part of every store lookup. Jobs and Tasks must expire coherently; a Task pointing at a pruned Job must return an honest terminal or expired outcome, not a fabricated completed result.

**Compatibility.** Clients not advertising the Tasks extension continue receiving existing Job IDs. `zemax_job_status/list/cancel` remain available and owner-authorized in scoped deployments; do not remove them or change their schemas as part of a Tasks rollout.

## Acceptance gates

1. Protocol negotiation: no opt-in → normal `tools/call`; explicit opt-in → Task only for reviewed long tools; legacy clients continue working.
2. Real status lifecycle: `tools/call → taskId → tasks/get → actual Worker Job terminal result`; assert no double execution.
3. Two-client isolation: cross-token `tasks/get/update/cancel` all fail without result/progress disclosure.
4. Token revocation, Host restart, Worker generation replacement, queue full, expiry, cancellation races, stale results and malformed payloads are all covered by tests.
5. Licensed OpticStudio functional fixtures verify long-running NSC/Tolerancing/optimization tasks on supported ZOS-API versions.

**Original assessment recommendation (superseded by the RC5 user-requested default):** Keep Tasks behind a disabled-by-default feature flag until these gates pass. The release acceptance gates above still apply; enabling the candidate does not prove them complete.

## Task-to-Job ledger foundation (2026-10-08)

The original non-runtime-changing foundation branch added `WorkerTaskLedger` and host-level regression assertions. At that historical stage **Official Tasks remained disabled** and the ledger was not registered with the MCP transport. The integrated adapter described below supersedes that stage and adds no duplicate Worker execution path.

The ledger enforces immutable (`owner`, `workerGeneration`, `jobId`) association; a task is still `working` when only a Job ID or terminal progress event exists, until its **actual** `CallToolResult` has been retrieved. Domain errors retain `completed` plus `isError:true`. Cancellation is advisory until Worker confirmation; generation loss marks unfinished tasks `failed`; terminal transitions are idempotent. Active tasks are never evicted just to admit new ones. Both Task entries and retained result references are bounded; an evicted result is explicitly marked expired.

The new `WorkerTaskLedgerAssertions` cover cross-owner isolation, wrong-generation attempts, duplicate Job linkage, missing-result failures, premature completion, cancellation races, recovery, result expiry and saturated admission. They are run by the existing Private RPC test executable; **they are not an end-to-end Tasks protocol or licensed ZOS-API test**.

Original foundation integration checklist (implemented by the adapter below; licensed validation limits remain separate):

1. Build a dedicated `ModelContextProtocol.Extensions.Tasks` integration using a **custom alternate-result tool handler**, not SDK auto-wrapping. Only explicitly allowed long-running tools may advertise/return a Task on a negotiated 2026-07-28 connection with per-call opt-in; other clients receive the original Job ID.
2. Ensure task get/update/cancel handlers use authenticated owner identity on *every* lookup, with credential revocation and result confidentiality verified against real stateless HTTP connections. Never use the SDK's unscoped in-memory Tasks store in scoped mode.
3. On tasks/get, retrieve and validate the Job's final payload (including `ResultExpired`) and convert it to the **actual** `CallToolResult`. Task cancellation must call the existing owner-authorized Job cancel path; no second ZOS-API job is created.
4. Add true protocol negotiation, dual-client E2E, Worker-restart/cancel race, bounded results, and live NSC/Tolerancing tests before flipping the default-off capability flag. The ledger can be wired to Worker generation/status events only as part of this protocol bridge.

## Optional official protocol adapter (2026-10-08, experimental)

The integrated protocol adapter runs on top of the bounded
`WorkerTaskLedger`. **RC5 defaults to ON**; turn it off explicitly with
`--enable-official-tasks false` or the Launcher checkbox. When disabled,
the SDK Tasks extension is not advertised or registered, and traditional
`zemax_job_status/list/cancel` remain unchanged.

With the feature flag enabled, only these reviewed asynchronous tools may
return an official Task handle on an MCP `2026-07-28` request carrying the
`io.modelcontextprotocol/tasks` per-request client capability:
`zemax_run_nsc_ray_trace`, `zemax_run_tolerancing`, `zemax_pop`,
`zemax_global_search`, and `zemax_multistart_optimize`. In every other
case the original `CallToolResult` (including the legacy `jobId`) is
returned. No ZOS-API operation is launched twice.

The Host implements `tasks/get`, `tasks/update` and `tasks/cancel` using
official SDK protocol models and the custom alternate-result handler rather
than `WithTasks`. Every method checks the current request's protocol and
opt-in plus its **transport-authenticated owner**. `tasks/get` polls the
single underlying Job with `zemax_job_status`, validates its ID and state,
and serializes its actual completed result as a `CallToolResult`. A pruned,
oversized, malformed or expired result is reported explicitly as unavailable.
`tasks/cancel` uses `zemax_job_cancel`; the status remains working until
the Worker confirms cancellation, and an already-completed Task is immutable.
`tasks/update` acknowledges empty input only: the current Worker Job tools
do not expose sampling/elicitation, so unsolicited input is rejected.

The private RPC HTTP fixture now covers per-request negotiation, synchronous
fallback, two distinct bearer tokens with deliberately identical spoofed
clientInfo/instance IDs, cross-token `tasks/get/update/cancel` denials,
real Worker result retrieval, late cancellation, cooperative cancellation,
revocation and duplicate execution checks. These are **fake-Worker tests**
and are not equivalent to licensed OpticStudio functional verification.

Licensed NSC/Tolerancing completion and cooperative Global Search cancellation
passed on deployed RC5 and RC6; RC7 was subsequently deployed by the user.
Windows CI at the final release commit, non-cooperative Worker hard recovery,
and other licensed version families remain separate gates. The user-requested
default does not make untested scenarios production-verified.

## Live acceptance and hard-recovery evidence (2026-10-08)

The existing `scripts/verify-live-functional.ps1` now supports these explicit additional gates:

- `-VerifyOfficialTasks -VerifyNsc`: a real asynchronous NSC ray trace must return a Task handle, remain pollable and complete with the *actual* ZOS-API result (`success`, `state`, `runtimeSeconds`).
- `-VerifyOfficialTasks -VerifyTolerance`: real asynchronous Sensitivity/Monte Carlo with structured final result; fails if the final result contains no Monte Carlo rows.
- `-VerifyOfficialTasks -VerifyBackgroundJobs -VerifyTaskCancellation`: a long Global Search (`timeoutSeconds=0`) is cancelled through `tasks/cancel` and reaches a bounded terminal state. A cancellation that races with completion **does not pass** this strict gate; use an adequately complex fixture. An observed Worker generation change is required if cancellation instead produces a hard-recovery failure.
- `-VerifyWorkerCrashRecovery -AllowWorkerTermination` together with `-VerifyOfficialTasks` and a long NSC or Global Search fixture: **destructively terminates the dedicated Worker process** after Task creation; validates generation change, old Task failure, and fresh Worker response. Run on a dedicated licensed validation machine only. This tests forced process-loss recovery, **not** the cancellation grace timer.

All these tests require `-AllowReplaceCurrentSystem` and operate on a temporary fixture copy. For live recovery, use local/shared-token Host mode with full diagnostic health; scoped mode intentionally redacts Worker PID/generation. RC5 defaults Tasks on; real official Tasks acceptance remains pending.

The existing CI `VerifyJobHardRecoveryAsync` separately injects a deliberately non-cooperative background Job with a short cancellation grace and asserts a failed terminal state and hard-recovery callback. The private RPC tests cover Worker-generation fault, immediate control-lease handoff and cross-token Task confidentiality.

**Do not report actual licensed OpticStudio runs as PASS until the resulting JSON evidence exists.** Hosted Windows CI validates syntax and fake-Worker/simulation behavior only.
