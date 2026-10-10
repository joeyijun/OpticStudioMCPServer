using System.Text.Json;
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
    private int _reservedAdmissions;
    private readonly string? _journalPath;
    private DateTimeOffset _lastJournalWrite;

    /// <summary>
    /// Reserve capacity BEFORE a negotiated Task starts its underlying Worker
    /// job. A saturated ledger must reject explicitly, not silently switch
    /// an opted-in Task call to the legacy Job-ID response.
    /// </summary>
    internal IDisposable? TryReserveAdmission()
    {
        lock (_gate)
        {
            var active = _tasks.Values.Count(entry => !IsTerminal(entry.State));
            if (active + _reservedAdmissions >= _maxRecords) return null;
            _reservedAdmissions++;
            return new AdmissionReservation(this);
        }
    }

    private sealed class AdmissionReservation : IDisposable
    {
        private WorkerTaskLedger? _owner;
        internal AdmissionReservation(WorkerTaskLedger owner) => _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;
            lock (owner._gate) owner._reservedAdmissions--;
        }
    }

    internal WorkerTaskLedger(
        int maxRecords = DefaultMaxRecords,
        int maxRetainedResults = DefaultMaxRetainedResults,
        string? journalPath = null)
    {
        if (maxRecords < 1) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        if (maxRetainedResults < 0 || maxRetainedResults > maxRecords)
            throw new ArgumentOutOfRangeException(nameof(maxRetainedResults));
        _maxRecords = maxRecords;
        _maxRetainedResults = maxRetainedResults;
        _journalPath = string.IsNullOrWhiteSpace(journalPath) ? null : journalPath;
        RestoreJournal();
    }

    // Local, owner-scoped metadata only. Never persist a raw optical result,
    // bearer token, ZOS file path or per-task binary payload.
    private sealed class JournalRecord
    {
        public string TaskId { get; set; } = "";
        public string Owner { get; set; } = "";
        public string JobId { get; set; } = "";
        public long Generation { get; set; }
        public long Sequence { get; set; }
        public string State { get; set; } = "working";
        public string Message { get; set; } = "";
        public bool CancelRequested { get; set; }
        public bool ResultExpired { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private void RestoreJournal()
    {
        if (_journalPath == null || !File.Exists(_journalPath)) return;
        try
        {
            var file = new FileInfo(_journalPath);
            if (file.Length > 1024 * 1024)
                throw new InvalidDataException("Task journal exceeds its one-megabyte safety bound.");
            var saved = JsonSerializer.Deserialize<JournalRecord[]>(File.ReadAllText(_journalPath))
                ?? Array.Empty<JournalRecord>();
            foreach (var record in saved.OrderBy(x => x.Sequence).TakeLast(_maxRecords))
            {
                if (record.TaskId.Length is < 1 or > 128 ||
                    record.JobId.Length is < 1 or > 128 ||
                    record.Owner.Length is < 1 or > 512 ||
                    record.Generation <= 0 || record.Sequence <= 0 ||
                    record.CreatedAt == default || record.UpdatedAt == default ||
                    _tasks.ContainsKey(record.TaskId) ||
                    _byJob.ContainsKey((record.Generation, record.JobId)))
                    continue;
                var entry = new Entry(record.TaskId, record.Owner, record.JobId,
                    record.Generation, record.Sequence)
                {
                    CreatedAt = record.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CancelRequested = record.CancelRequested,
                    // Results are deliberately never stored in the journal.
                    ResultExpired = record.ResultExpired || record.State == "completed"
                };
                if (IsTerminal(record.State))
                {
                    entry.State = record.State;
                    entry.Message = record.Message;
                }
                else
                {
                    // MCP Task protocol supports failed, not interrupted, as a
                    // terminal Task state. Preserve the interruption reason.
                    entry.State = "failed";
                    entry.Message = "Host restarted; the previous Worker Job was interrupted and cannot resume.";
                }
                _tasks.Add(entry.TaskId, entry);
                _byJob.Add((entry.Generation, entry.JobId), entry.TaskId);
                _sequence = Math.Max(_sequence, entry.Sequence);
            }
            PersistLocked(force: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not restore the bounded, metadata-only Task journal");
        }
    }

    // Mutations are serialized under _gate; replace a complete temporary file
    // atomically. Throttle progress chatter, but always flush lifecycle changes.
    // Journal failures do not turn successful Worker execution into an error.
    private void PersistLocked(bool force = false)
    {
        if (_journalPath == null) return;
        if (!force && DateTimeOffset.UtcNow - _lastJournalWrite < TimeSpan.FromSeconds(5)) return;
        try
        {
            var folder = Path.GetDirectoryName(_journalPath)
                ?? throw new InvalidOperationException("Journal path has no parent directory.");
            Directory.CreateDirectory(folder);
            var records = _tasks.Values.OrderBy(x => x.Sequence).Select(x => new JournalRecord
            {
                TaskId = x.TaskId, Owner = x.Owner, JobId = x.JobId,
                Generation = x.Generation, Sequence = x.Sequence,
                State = x.State,
                // Diagnostic payloads may contain lens paths or private data;
                // the persistent journal stores status categories only.
                Message = x.State switch
                {
                    "completed" => "Completed; result not persisted.",
                    "cancelled" => "Cancelled.",
                    "failed" => "Failed; inspect live logs for the original reason.",
                    _ => "Working at previous Host shutdown."
                },
                CancelRequested = x.CancelRequested, ResultExpired = x.ResultExpired,
                CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt
            }).ToArray();
            var json = JsonSerializer.Serialize(records);
            var temp = _journalPath + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(_journalPath))
                File.Replace(temp, _journalPath, null);
            else File.Move(temp, _journalPath);
            _lastJournalWrite = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to persist Task status journal; live memory state remains authoritative");
        }
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
            PersistLocked(force: true);
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
    /// Bounded, payload-free diagnostic view for the authenticated owner.
    /// Never expose raw Task results or another credential's Task IDs.
    /// </summary>
    internal IReadOnlyList<object> ListOwnedMetadata(string owner, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(owner)) return Array.Empty<object>();
        limit = Math.Max(1, Math.Min(limit, 25));
        lock (_gate)
            return _tasks.Values
                .Where(entry => string.Equals(entry.Owner, owner, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.Sequence)
                .Take(limit)
                .Select(entry => (object)new
                {
                    taskId = entry.TaskId,
                    jobId = entry.JobId,
                    generation = entry.Generation,
                    state = entry.State,
                    message = entry.Message,
                    createdAt = entry.CreatedAt,
                    updatedAt = entry.UpdatedAt,
                    cancelRequested = entry.CancelRequested,
                    resultExpired = entry.ResultExpired
                }).ToArray();
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
            PersistLocked(force: true);
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

            switch (job.State?.ToUpperInvariant())
            {
                case "QUEUED":
                case "RUNNING":
                case "CANCELLING":
                    if (!entry.WorkerFinished)
                        entry.Message = job.Message ?? job.State;
                    break;
                case "COMPLETED":
                    // Worker completion is not the actual CallToolResult. Until
                    // it is fetched and validated, tasks/get must say working.
                    entry.WorkerFinished = true;
                    entry.Message = "Worker Job completed; waiting for the actual tool result.";
                    break;
                case "CANCELLED":
                    FinishLocked(entry, "cancelled", job.Message ?? "Worker Job cancelled.", null);
                    break;
                case "FAILED":
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
            PersistLocked(force: IsTerminal(entry.State) || entry.WorkerFinished);
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
            PersistLocked(force: true);
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
            PersistLocked(force: true);
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
            var changed = false;
            foreach (var entry in _tasks.Values.Where(entry =>
                         entry.Generation == generation && !IsTerminal(entry.State)))
            {
                FinishLocked(entry, "failed", "The Worker generation ended before the Task result was available.", null);
                changed = true;
            }
            if (changed) PersistLocked(force: true);
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
        internal DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
