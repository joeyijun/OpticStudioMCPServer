using ModelContextProtocol.Protocol;
using ZemaxMCP.HttpBridge.ModernHost;
using ZemaxMCP.Rpc;

namespace ZemaxMCP.PrivateRpcTests;

internal static class WorkerTaskLedgerAssertions
{
    internal static void Verify()
    {
        VerifyAuthorizationAndCompletion();
        VerifyCancellationAndGenerationFailure();
        VerifyDomainFailureAndExpiredResults();
        VerifyBoundedAdmission();
    }

    private static void VerifyAuthorizationAndCompletion()
    {
        var ledger = new WorkerTaskLedger();
        Assert(ledger.TryRegister("scoped:alice", "job-1", 5, out var task) && task != null,
            "A valid Worker Job must create one Task.");
        var taskId = task!.TaskId;
        var visible = System.Text.Json.JsonSerializer.Serialize(ledger.ListOwnedMetadata("scoped:alice"));
        var invisible = System.Text.Json.JsonSerializer.Serialize(ledger.ListOwnedMetadata("scoped:bob"));
        if (!visible.Contains(taskId, StringComparison.Ordinal) ||
            visible.Contains("real-optical-result", StringComparison.Ordinal) ||
            invisible.Contains(taskId, StringComparison.Ordinal) ||
            ledger.ListOwnedMetadata("").Count != 0)
            throw new InvalidOperationException("Owner-scoped Task diagnostics must expose only owned identifiers and no result payloads.");
        Assert(task.State == "working" && task.Result == null,
            "A newly registered Task must not complete on receiving the Job ID.");
        Assert(!ledger.TryGet("scoped:bob", taskId, out _) &&
               !ledger.TryGet("scoped:alice", "guessed-id", out _),
            "Foreign or unknown Task IDs must be indistinguishable to the caller.");
        Assert(!ledger.TryRequestCancel("scoped:bob", taskId, 5, out _) &&
               !ledger.TryRequestCancel("scoped:alice", taskId, 6, out _),
            "Cancellation must require the Task owner and the active Worker generation.");
        var actual = Result("real-optical-result");
        Assert(!ledger.TryComplete("scoped:alice", taskId, "job-1", 5, actual),
            "A Worker Job ID alone must not complete a Task.");
        ledger.ObserveJob(5, new WorkerJobStatus { JobId = "job-1", State = "Running", Message = "tracing" });
        Assert(ledger.TryGet("scoped:alice", taskId, out var running) &&
               running!.State == "working" && running.Message == "tracing",
            "Worker execution updates must be preserved.");
        ledger.ObserveJob(5, new WorkerJobStatus { JobId = "job-1", State = "Completed" });
        Assert(ledger.TryGet("scoped:alice", taskId, out var waiting) &&
               waiting!.State == "working" && waiting.Result == null,
            "Task must wait for the real result after the Worker reports Completed.");
        Assert(!ledger.TryComplete("scoped:bob", taskId, "job-1", 5, actual) &&
               !ledger.TryComplete("scoped:alice", taskId, "job-2", 5, actual) &&
               !ledger.TryComplete("scoped:alice", taskId, "job-1", 6, actual),
            "A result must match immutable owner, Job ID and generation.");
        Assert(ledger.TryComplete("scoped:alice", taskId, "job-1", 5, actual),
            "A validated real result must complete the Task.");
        Assert(ledger.TryGet("scoped:alice", taskId, out var completed) &&
               completed!.State == "completed" && ReferenceEquals(completed.Result, actual) &&
               completed.Result!.IsError != true,
            "The final Task result must retain the actual CallToolResult.");
        Assert(!ledger.TryComplete("scoped:alice", taskId, "job-1", 5, Result("overwrite")) &&
               !ledger.TryRequestCancel("scoped:alice", taskId, 5, out _),
            "Terminal completion must be idempotent and protected from late cancellation.");
        ledger.ObserveJob(5, new WorkerJobStatus { JobId = "job-1", State = "Cancelled" });
        ledger.ReleaseGeneration(5);
        Assert(ledger.TryGet("scoped:alice", taskId, out var stillCompleted) &&
               ReferenceEquals(stillCompleted!.Result, actual),
            "Late Worker events and generation loss must not destroy a terminal result.");
        ExpectInvalidOperation(() => ledger.TryRegister("scoped:bob", "job-1", 5, out _),
            "A Worker Job cannot be rebound to a different owner.");
    }

    private static void VerifyCancellationAndGenerationFailure()
    {
        var ledger = new WorkerTaskLedger();
        Assert(ledger.TryRegister("scoped:alice", "cancel-job", 7, out var task), "Register failed.");
        Assert(ledger.TryRequestCancel("scoped:alice", task!.TaskId, 7, out var workerJob) &&
               workerJob == "cancel-job", "Cancellation must resolve to the one underlying Job.");
        Assert(ledger.TryGet("scoped:alice", task.TaskId, out var requested) &&
               requested!.State == "working" && requested.CancelRequested,
            "Cancellation acknowledgement must not prematurely set Cancelled.");
        ledger.ObserveJob(7, new WorkerJobStatus { JobId = "cancel-job", State = "Cancelled" });
        Assert(ledger.TryGet("scoped:alice", task.TaskId, out var cancelled) &&
               cancelled!.State == "cancelled" && cancelled.Result == null,
            "Only a Worker cancellation confirmation may make Task cancelled.");
        Assert(!ledger.TryComplete("scoped:alice", task.TaskId, "cancel-job", 7, Result("late")),
            "Late results cannot overwrite cancelled Tasks.");

        Assert(ledger.TryRegister("scoped:bob", "hung-job", 8, out var hung), "Register failed.");
        ledger.ReleaseGeneration(8);
        Assert(ledger.TryGet("scoped:bob", hung!.TaskId, out var failed) &&
               failed!.State == "failed" && failed.Message.Contains("generation", StringComparison.OrdinalIgnoreCase),
            "A failed Worker generation must terminally fail active Tasks.");
        Assert(!ledger.TryGet("scoped:alice", hung.TaskId, out _) &&
               !ledger.TryRequestCancel("scoped:bob", hung.TaskId, 8, out _),
            "Failed Tasks must remain owner-scoped and uncancellable.");
        ledger.ObserveJob(8, new WorkerJobStatus { JobId = "hung-job", State = "Completed" });
        Assert(ledger.TryGet("scoped:bob", hung.TaskId, out var afterLate) && afterLate!.State == "failed",
            "A stale Worker event must not resurrect a Task.");
    }

    private static void VerifyDomainFailureAndExpiredResults()
    {
        var ledger = new WorkerTaskLedger(maxRecords: 4, maxRetainedResults: 1);
        Assert(ledger.TryRegister("scoped:a", "job-a", 9, out var a), "Register failed.");
        ledger.ObserveJob(9, new WorkerJobStatus { JobId = "job-a", State = "Failed", Message = "bad optical operand" });
        Assert(ledger.TryGet("scoped:a", a!.TaskId, out var failedDomain) &&
               failedDomain!.State == "completed" && failedDomain.Result?.IsError == true &&
               ((TextContentBlock)failedDomain.Result.Content.Single()).Text == "bad optical operand",
            "A Worker tool-domain error must produce completed with isError=true.");

        Assert(ledger.TryRegister("scoped:a", "job-b", 9, out var b), "Register failed.");
        ledger.ObserveJob(9, new WorkerJobStatus { JobId = "job-b", State = "Completed" });
        var realDomainError = Result("structured-domain-error", true);
        Assert(ledger.TryComplete("scoped:a", b!.TaskId, "job-b", 9, realDomainError),
            "Worker results with isError=true must still complete.");
        Assert(ledger.TryGet("scoped:a", b.TaskId, out var bDone) &&
               ReferenceEquals(bDone!.Result, realDomainError) && bDone.State == "completed",
            "isError and the real result must survive the bridge.");
        Assert(ledger.TryGet("scoped:a", a.TaskId, out var expired) &&
               expired!.State == "completed" && expired.ResultExpired && expired.Result == null,
            "Evicted terminal result must be explicitly marked expired, not fabricated.");

        Assert(ledger.TryRegister("scoped:a", "job-c", 9, out var c), "Register failed.");
        Assert(ledger.FailUnavailableResult("scoped:a", c!.TaskId, 9, "Worker Job history pruned."),
            "Pruned Worker history must have an explicit terminal outcome.");
        Assert(ledger.TryGet("scoped:a", c.TaskId, out var unavailable) &&
               unavailable!.State == "failed" && unavailable.Result == null,
            "Pruned/malformed results must never become invented completed results.");
        Assert(!ledger.FailUnavailableResult("scoped:a", b.TaskId, 9, "cannot replace result"),
            "A late expiry must not overwrite completed Tasks.");
    }

    private static void VerifyBoundedAdmission()
    {
        var ledger = new WorkerTaskLedger(maxRecords: 2, maxRetainedResults: 1);
        Assert(ledger.TryRegister("scoped:a", "active-1", 11, out var first), "Register failed.");
        Assert(ledger.TryRegister("scoped:b", "active-2", 11, out var second), "Register failed.");
        Assert(!ledger.TryRegister("scoped:c", "active-3", 11, out var denied) && denied == null,
            "A full ledger must reject admission rather than evict active Tasks.");
        ledger.ObserveJob(11, new WorkerJobStatus { JobId = "active-1", State = "Cancelled" });
        Assert(ledger.TryRegister("scoped:c", "active-3", 11, out _),
            "A completed record should be evicted to admit new work.");
        Assert(!ledger.TryGet("scoped:a", first!.TaskId, out _) &&
               ledger.TryGet("scoped:b", second!.TaskId, out var active) &&
               active!.State == "working",
            "Capacity eviction must drop only terminal records.");
        ledger.ObserveJob(11, new WorkerJobStatus { JobId = "active-3", State = "Unknown" });
        ExpectArgument(() => ledger.TryRegister("scoped:c", "active-1", 0, out _),
            "An invalid Worker generation must be rejected.");
    }

    private static CallToolResult Result(string message, bool error = false) => new()
    {
        IsError = error,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } }
    };

    private static void Assert(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    private static void ExpectArgument(Action action, string error)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(error);
    }

    private static void ExpectInvalidOperation(Action action, string error)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(error);
    }
}
