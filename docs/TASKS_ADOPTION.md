# Official MCP Tasks adoption assessment

Status: **evaluated, not enabled**. The server remains on ordinary `tools/call` plus the existing `zemax_job_status`, `zemax_job_list`, and `zemax_job_cancel` compatibility path.

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

**Recommendation:** Keep Tasks behind a disabled-by-default feature flag in a separate PR until these gates pass. The immediate PR hardens the existing Job path and prevents scoped clients from using the legacy process-global multistart status/stop bypass.
