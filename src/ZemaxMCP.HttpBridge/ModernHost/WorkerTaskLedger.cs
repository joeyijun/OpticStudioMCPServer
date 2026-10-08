using ModelContextProtocol.Protocol;
using ZemaxMCP.Rpc;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Opt-in MCP Tasks bridge foundation. A Task observes one existing Worker Job;
/// it NEVER schedules another copy of a ZOS-API operation. This ledger is not
/// registered as an MCP Tasks handler until the protocol/security gates pass.
/// </summary>
internal sealed class WorkerTaskLedger
{
    internal const int DefaultMaxRecords = 256;
    internal const int DefaultMaxRetainedResults = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<(long Generation, string JobId), string> _byJob = new();
    private readonly int _maxRecords;
    private readonly int _maxRetainedResults;
    private long _sequence;

    internal WorkerTaskLedger(
        int maxRecords = DefaultMaxRecords,
        int maxRetainedResults = DefaultMaxRetainedResults)
    {
        if (maxRecords < 1) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        if (maxRetainedResults < 0 || maxRetainedResults > maxRecords)
            throw new ArgumentOutOfRangeException(nameof(maxRetainedResults));
        _maxRecords = maxRecords;
        _maxRetainedResults = maxRetainedResults;
    }

    /// <summary>
    /// Register only AFTER the already-authorized Worker tool returns its Job ID.
    /// Admission fails closed when all retained records are still active.
    /// </summary>
    internal bool TryRegister(string owner, string jobId, long generation, out WorkerTaskSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(jobId) ||
            jobId.Length > 128 || generation <= 0)
            throw new ArgumentException("A Task requires a valid authenticated owner, Worker Job ID, and generation.");

        lock (_gate)
        {
            if (_byJob.ContainsKey((generation, jobId)))
                throw new InvalidOperationException("A Worker Job may be linked to only one Task.");

            if (_tasks.Count >= _maxRecords)
            {
                var oldestTerminal = _tasks.Values
                    .Where(entry => IsTerminal(entry.State))
                    .OrderBy(entry => entry.Sequence).FirstOrDefault();
                if (oldestTerminal == null)
                {
                    snapshot = null;
                    return false;
                }
                _tasks.Remove(oldestTerminal.TaskId);
                _byJob.Remove((oldestTerminal.Generation, oldestTerminal.JobId));
            }

            var entry = new Entry(Guid.NewGuid().ToString("N"), owner, jobId, generation, ++_sequence);
            _tasks.Add(entry.TaskId, entry);
            _byJob.Add((generation, jobId), entry.TaskId);
            snapshot = Snapshot(entry);
            return true;
        }
    }

    // Never look up by Task ID alone in a request handler. Authentication is
    // mandatory even for terminal Tasks and expired results.
    internal bool TryGet(string owner, string taskId, out WorkerTaskSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (!TryOwnedLocked(owner, taskId, out var entry))
            {
                snapshot = null;
                return false;
            }
            snapshot = Snapshot(entry!);
            return true;
        }
    }

    /// <summary>
    /// Return the underlying Job ID for the existing owner-authorized cancel
    /// tool. Do not claim the Task is cancelled until the Worker confirms it.
    /// </summary>
    internal bool TryRequestCancel(string owner, string taskId, long currentGeneration, out string jobId)
    {
        jobId = string.Empty;
        lock (_gate)
        {
            if (!TryOwnedLocked(owner, taskId, out var entry) ||
                entry!.Generation != currentGeneration || IsTerminal(entry.State))
                return false;
            entry.CancelRequested = true;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            jobId = entry.JobId;
            return true;
        }
    }

    internal void ObserveJob(long generation, WorkerJobStatus job)
    {
        if (generation <= 0 || string.IsNullOrWhiteSpace(job.JobId)) return;
        lock (_gate)
        {
            if (!_byJob.TryGetValue((generation, job.JobId), out var taskId) ||
                !_tasks.TryGetValue(taskId, out var entry) || IsTerminal(entry.State))
                return;

            switch (job.State)
            {
                case "Queued":
                case "Running":
                case "Cancelling":
                    if (!entry.WorkerFinished)
                        entry.Message = job.Message ?? job.State;
                    break;
                case "Completed":
                    // Worker completion is not the actual CallToolResult. Until
                    // it is fetched and validated, tasks/get must say working.
                    entry.WorkerFinished = true;
                    entry.Message = "Worker Job completed; waiting for the actual tool result.";
                    break;
                case "Cancelled":
                    FinishLocked(entry, "cancelled", job.Message ?? "Worker Job cancelled.", null);
                    break;
                case "Failed":
                    // A Worker Job exception is a tool-domain error, not a
                    // JSON-RPC protocol failure. Retain isError=true as the result.
                    FinishLocked(entry, "completed", job.Message ?? "Worker Job failed.",
                        ToolError(job.Message ?? "Worker Job failed."));
                    TrimResultsLocked();
                    break;
                default:
                    // A malformed/unrecognised Worker state cannot advance a Task.
                    return;
            }
            entry.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Only the matching owner, generation and completed Worker Job may publish
    /// its real result. Includes domain-error CallToolResults unchanged.
    /// </summary>
    internal bool TryComplete(string owner, string taskId, string jobId, long generation, CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (!TryOwnedLocked(owner, taskId, out var entry) ||
                entry!.Generation != generation ||
                !string.Equals(entry.JobId, jobId, StringComparison.Ordinal) ||
                IsTerminal(entry.State) || !entry.WorkerFinished)
                return false;

            FinishLocked(entry, "completed", "Completed.", result);
            TrimResultsLocked();
            return true;
        }
    }

    /// <summary>
    /// A pruned Worker result or malformed terminal payload must never become
    /// a fabricated success. This is a terminal protocol/bridge failure.
    /// </summary>
    internal bool FailUnavailableResult(string owner, string taskId, long generation, string reason)
    {
        lock (_gate)
        {
            if (!TryOwnedLocked(owner, taskId, out var entry) ||
                entry!.Generation != generation || IsTerminal(entry.State))
                return false;
            FinishLocked(entry, "failed", string.IsNullOrWhiteSpace(reason)
                ? "The Worker Job result is unavailable or expired." : reason, null);
            return true;
        }
    }

    /// <summary>
    /// A hard recovery invalidates active work. Retain an owner-visible failure
    /// instead of leaking a perpetually working Task or erasing its ownership.
    /// </summary>
    internal void ReleaseGeneration(long generation)
    {
        if (generation <= 0) return;
        lock (_gate)
        {
            foreach (var entry in _tasks.Values.Where(entry =>
                         entry.Generation == generation && !IsTerminal(entry.State)))
                FinishLocked(entry, "failed", "The Worker generation ended before the Task result was available.", null);
        }
    }

    private bool TryOwnedLocked(string owner, string taskId, out Entry? entry)
    {
        entry = null;
        return !string.IsNullOrWhiteSpace(owner) &&
               !string.IsNullOrWhiteSpace(taskId) &&
               _tasks.TryGetValue(taskId, out entry) &&
               string.Equals(entry.Owner, owner, StringComparison.Ordinal);
    }

    private static bool IsTerminal(string state) => state is "completed" or "cancelled" or "failed";

    private static CallToolResult ToolError(string message) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } }
    };

    private static void FinishLocked(Entry entry, string state, string message, CallToolResult? result)
    {
        entry.State = state;
        entry.Message = message;
        entry.Result = result;
        entry.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void TrimResultsLocked()
    {
        foreach (var entry in _tasks.Values
                     .Where(entry => entry.Result != null)
                     .OrderByDescending(entry => entry.UpdatedAt)
                     .ThenByDescending(entry => entry.Sequence)
                     .Skip(_maxRetainedResults))
        {
            entry.Result = null;
            entry.ResultExpired = true;
        }
    }

    private static WorkerTaskSnapshot Snapshot(Entry entry) =>
        new(entry.TaskId, entry.JobId, entry.Generation, entry.State,
            entry.Message, entry.CancelRequested, entry.ResultExpired,
            entry.CreatedAt, entry.UpdatedAt, entry.Result);

    private sealed class Entry
    {
        internal Entry(string taskId, string owner, string jobId, long generation, long sequence)
        {
            TaskId = taskId;
            Owner = owner;
            JobId = jobId;
            Generation = generation;
            Sequence = sequence;
        }

        internal string TaskId { get; }
        internal string Owner { get; }
        internal string JobId { get; }
        internal long Generation { get; }
        internal long Sequence { get; }
        internal DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        internal DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        internal string State { get; set; } = "working";
        internal string Message { get; set; } = "Queued.";
        internal bool WorkerFinished { get; set; }
        internal bool CancelRequested { get; set; }
        internal bool ResultExpired { get; set; }
        internal CallToolResult? Result { get; set; }
    }
}

internal sealed record WorkerTaskSnapshot(
    string TaskId, string JobId, long Generation, string State, string Message,
    bool CancelRequested, bool ResultExpired,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, CallToolResult? Result);
