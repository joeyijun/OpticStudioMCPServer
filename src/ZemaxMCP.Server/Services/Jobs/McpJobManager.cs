using System.Collections.Generic;
using System.Diagnostics;

namespace ZemaxMCP.Server.Services.Jobs;

/// <summary>
/// Runs potentially long MCP operations one at a time, exposes queue state to
/// clients, and provides cooperative cancellation without restarting the MCP
/// process. ZOS-API work still enters the dedicated STA dispatcher separately.
/// </summary>
public sealed class McpJobManager : IDisposable
{
    private static readonly AsyncLocal<string?> ParentOperation = new();

    public const int DefaultMaxHistory = 128;
    public const int DefaultMaxResultHistory = 16;
    public const int DefaultMaxPending = 64;

    private readonly object _gate = new();
    private readonly Queue<JobEntry> _pending = new();
    private readonly Dictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly int _maxHistory;
    private readonly int _maxResultHistory;
    private readonly int _maxPending;
    private readonly TimeSpan _cancellationGrace;
    private readonly Action<McpJobSnapshot>? _hardRecoveryAction;
    private bool _processorRunning;
    private bool _recoveryRequired;
    private bool _disposed;

    public McpJobManager(
        int maxHistory = DefaultMaxHistory,
        int maxPending = DefaultMaxPending,
        TimeSpan? cancellationGrace = null,
        int maxResultHistory = DefaultMaxResultHistory,
        Action<McpJobSnapshot>? hardRecoveryAction = null)
    {
        if (maxHistory < 1) throw new ArgumentOutOfRangeException(nameof(maxHistory), "Job history limit must be at least 1.");
        if (maxResultHistory < 0) throw new ArgumentOutOfRangeException(nameof(maxResultHistory), "Job result history limit cannot be negative.");
        if (maxPending < 1) throw new ArgumentOutOfRangeException(nameof(maxPending), "Pending job limit must be at least 1.");
        _maxHistory = maxHistory;
        _maxResultHistory = Math.Min(maxResultHistory, maxHistory);
        _maxPending = maxPending;
        _cancellationGrace = cancellationGrace ?? TimeSpan.FromSeconds(60);
        if (_cancellationGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancellationGrace), "Job cancellation grace must be positive.");
        _hardRecoveryAction = hardRecoveryAction;
    }

    public event Action<McpJobSnapshot>? JobChanged;

    internal static IDisposable PushParentOperation(string? operationId)
    {
        var previous = ParentOperation.Value;
        ParentOperation.Value = string.IsNullOrWhiteSpace(operationId) ? null : operationId;
        return new ParentOperationScope(previous);
    }

    public McpJobSnapshot Enqueue(string toolName, Func<McpJobContext, Task> operation, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("A tool name is required.", nameof(toolName));
        if (operation == null) throw new ArgumentNullException(nameof(operation));

        JobEntry entry;
        McpJobSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_recoveryRequired)
                throw new InvalidOperationException("The prior background ZOS-API job did not drain; Worker hard recovery is required before another job may be queued.");
            if (_pending.Count >= _maxPending)
                throw new InvalidOperationException($"The background job queue is full ({_maxPending} pending jobs). Wait for a job to finish or cancel one before starting another.");
            entry = new JobEntry(Guid.NewGuid().ToString("N"), toolName, ParentOperation.Value, operation, timeout);
            _pending.Enqueue(entry);
            _jobs.Add(entry.Id, entry);
            snapshot = Snapshot(entry);
            if (!_processorRunning)
            {
                _processorRunning = true;
                _ = Task.Run(ProcessQueueAsync);
            }
        }
        Publish(snapshot);
        return snapshot;
    }

    public bool Cancel(string jobId, out McpJobSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var entry))
            {
                snapshot = null;
                return false;
            }
            if (entry.State is McpJobState.Completed or McpJobState.Cancelled or McpJobState.Failed)
            {
                snapshot = Snapshot(entry);
                return false;
            }
            entry.Cancellation.Cancel();
            entry.State = McpJobState.Cancelling;
            entry.Message = entry.StartedAt == null
                ? "Cancellation requested before the job started."
                : "Cancellation requested; the current ZOS-API operation will stop at its next safe cancellation point.";
            snapshot = Snapshot(entry);
        }
        Publish(snapshot);
        return true;
    }

    public McpJobSnapshot? Get(string jobId)
    {
        lock (_gate) return _jobs.TryGetValue(jobId, out var entry) ? Snapshot(entry) : null;
    }

    public IReadOnlyList<McpJobSnapshot> List()
    {
        lock (_gate) return _jobs.Values
            .OrderByDescending(x => x.QueuedAt)
            .Select(Snapshot)
            .ToArray();
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            JobEntry? entry;
            McpJobSnapshot? cancelledBeforeExecution = null;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _processorRunning = false;
                    return;
                }
                entry = _pending.Dequeue();
                if (entry.Cancellation.IsCancellationRequested)
                {
                    entry.State = McpJobState.Cancelled;
                    entry.CompletedAt = DateTimeOffset.UtcNow;
                    entry.Message = "Cancelled before execution.";
                    cancelledBeforeExecution = Snapshot(entry);
                    TrimHistoryLocked();
                }
                else
                {
                    entry.State = McpJobState.Running;
                    entry.StartedAt = DateTimeOffset.UtcNow;
                    entry.Message = "Running.";
                }
            }
            if (cancelledBeforeExecution != null)
            {
                Publish(cancelledBeforeExecution);
                continue;
            }
            Publish(Snapshot(entry));

            CancellationTokenSource? timeoutSource = null;
            try
            {
                timeoutSource = entry.Timeout is { } timeout
                    ? CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token)
                    : null;
                if (timeoutSource != null) timeoutSource.CancelAfter(entry.Timeout!.Value);
                var token = timeoutSource?.Token ?? entry.Cancellation.Token;
                var drained = await ExecuteWithRecoveryAsync(entry, token).ConfigureAwait(false);
                if (!drained)
                {
                    // Production hard recovery terminates the Worker generation.
                    // If a test/recovery observer returns instead, do not start
                    // another queued ZOS-API Job beside the still-hung task.
                    lock (_gate) _processorRunning = false;
                    return;
                }
                lock (_gate)
                {
                    entry.CompletedAt = DateTimeOffset.UtcNow;
                    if (token.IsCancellationRequested)
                    {
                        entry.State = McpJobState.Cancelled;
                        entry.Message = entry.Timeout is { } && timeoutSource?.IsCancellationRequested == true && !entry.Cancellation.IsCancellationRequested
                            ? "Timed out and stopped during the cancellation grace period."
                            : "Cancelled.";
                    }
                    else
                    {
                        entry.State = McpJobState.Completed;
                        entry.Message = "Completed.";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    entry.CompletedAt = DateTimeOffset.UtcNow;
                    entry.State = McpJobState.Cancelled;
                    entry.Message = entry.Timeout is { } && timeoutSource?.IsCancellationRequested == true && !entry.Cancellation.IsCancellationRequested
                        ? "Timed out and stopped at a safe cancellation point."
                        : "Cancelled.";
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    entry.CompletedAt = DateTimeOffset.UtcNow;
                    entry.State = McpJobState.Failed;
                    entry.Message = ex.Message;
                }
            }
            finally { timeoutSource?.Dispose(); }
            McpJobSnapshot terminal;
            lock (_gate)
            {
                terminal = Snapshot(entry);
                TrimHistoryLocked();
            }
            Publish(terminal);
        }
    }

    private async Task<bool> ExecuteWithRecoveryAsync(JobEntry entry, CancellationToken cancellationToken)
    {
        var context = new McpJobContext(
            cancellationToken,
            (progress, message) => PublishProgress(entry, progress, message),
            result => PublishResult(entry, result));

        var operationTask = entry.Operation(context);
        if (!cancellationToken.CanBeCanceled)
        {
            await operationTask.ConfigureAwait(false);
            return true;
        }

        var cancellationSignal = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (await Task.WhenAny(operationTask, cancellationSignal).ConfigureAwait(false) == operationTask)
        {
            await operationTask.ConfigureAwait(false);
            return true;
        }

        var drainDeadline = Task.Delay(_cancellationGrace);
        if (await Task.WhenAny(operationTask, drainDeadline).ConfigureAwait(false) == operationTask)
        {
            await operationTask.ConfigureAwait(false);
            return true;
        }

        McpJobSnapshot snapshot;
        McpJobSnapshot[] abandoned;
        lock (_gate)
        {
            // Quarantine this Worker generation BEFORE notifying the Host.
            // If hard-recovery action fails or returns in a test, neither a
            // new admission nor an already-queued job may run alongside the
            // COM call that ignored cancellation.
            _recoveryRequired = true;
            entry.CompletedAt = DateTimeOffset.UtcNow;
            entry.State = McpJobState.Failed;
            entry.Message = $"Cancellation did not stop the background ZOS-API job within {_cancellationGrace.TotalSeconds:0} seconds; Worker hard recovery is required.";
            snapshot = Snapshot(entry);
            var stopped = new List<McpJobSnapshot>();
            while (_pending.Count > 0)
            {
                var waiting = _pending.Dequeue();
                waiting.State = McpJobState.Failed;
                waiting.CompletedAt = DateTimeOffset.UtcNow;
                waiting.Message = "Worker generation requires hard recovery; queued job was not executed.";
                stopped.Add(Snapshot(waiting));
            }
            abandoned = stopped.ToArray();
            TrimHistoryLocked();
        }
        Publish(snapshot);
        foreach (var waiting in abandoned) Publish(waiting);
        try { _hardRecoveryAction?.Invoke(snapshot); }
        catch { /* The job state is already terminal; recovery observers must not corrupt it. */ }
        return false;
    }

    private void TrimHistoryLocked()
    {
        var terminal = _jobs.Values
            .Where(entry => entry.State is McpJobState.Completed or McpJobState.Cancelled or McpJobState.Failed)
            .OrderByDescending(entry => entry.CompletedAt ?? DateTimeOffset.MaxValue)
            .ToArray();

        // Retain metadata for a useful recent history window, but retain
        // potentially large result payloads for a much smaller newest subset.
        // Callers can distinguish a genuinely null result from an expired one.
        foreach (var entry in terminal.Skip(_maxResultHistory))
        {
            if (!entry.ResultWasSet || entry.ResultExpired) continue;
            entry.Result = null;
            entry.ResultExpired = true;
        }

        foreach (var entry in terminal.Skip(_maxHistory))
        {
            if (!_jobs.Remove(entry.Id)) continue;
            entry.Result = null;
            entry.Cancellation.Dispose();
        }
    }

    private void PublishProgress(JobEntry entry, double? progress, string? message)
    {
        McpJobSnapshot snapshot;
        lock (_gate)
        {
            if (entry.State is McpJobState.Completed or McpJobState.Cancelled or McpJobState.Failed)
                return; // a late, non-cooperative COM completion cannot resurrect a terminal job
            entry.Progress = progress;
            if (!string.IsNullOrWhiteSpace(message)) entry.Message = message!;
            snapshot = Snapshot(entry);
        }
        Publish(snapshot);
    }

    private void PublishResult(JobEntry entry, object? result)
    {
        McpJobSnapshot snapshot;
        lock (_gate)
        {
            if (entry.State is McpJobState.Completed or McpJobState.Cancelled or McpJobState.Failed)
                return;
            entry.Result = result;
            entry.ResultWasSet = true;
            entry.ResultExpired = false;
            snapshot = Snapshot(entry);
        }
        Publish(snapshot);
    }

    private McpJobSnapshot Snapshot(JobEntry entry) => new(
        entry.Id, entry.ToolName, entry.ParentOperationId, entry.State, entry.QueuedAt, entry.StartedAt, entry.CompletedAt,
        entry.Progress, entry.Message, QueuePosition(entry), entry.StartedAt == null ? null : DateTimeOffset.UtcNow - entry.StartedAt.Value,
        entry.ResultExpired, entry.Result);

    private int QueuePosition(JobEntry entry)
    {
        if (entry.State != McpJobState.Queued && entry.State != McpJobState.Cancelling) return 0;
        var position = 1;
        foreach (var queued in _pending)
        {
            if (ReferenceEquals(queued, entry)) return position;
            if (!queued.Cancellation.IsCancellationRequested) position++;
        }
        return 0;
    }

    private void Publish(McpJobSnapshot snapshot)
    {
        try { JobChanged?.Invoke(snapshot); } catch { /* Observers must not affect jobs. */ }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(McpJobManager));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _jobs.Values)
            {
                if (entry.State is McpJobState.Completed or McpJobState.Cancelled or McpJobState.Failed)
                    entry.Cancellation.Dispose();
                else
                    entry.Cancellation.Cancel();
            }
        }
    }

    private sealed class ParentOperationScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;
        public ParentOperationScope(string? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ParentOperation.Value = _previous;
        }
    }

    private sealed class JobEntry
    {
        public JobEntry(string id, string toolName, string? parentOperationId, Func<McpJobContext, Task> operation, TimeSpan? timeout)
        {
            Id = id;
            ToolName = toolName;
            ParentOperationId = parentOperationId;
            Operation = operation;
            Timeout = timeout;
        }

        public string Id { get; }
        public string ToolName { get; }
        public string? ParentOperationId { get; }
        public Func<McpJobContext, Task> Operation { get; }
        public TimeSpan? Timeout { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTimeOffset QueuedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public McpJobState State { get; set; } = McpJobState.Queued;
        public double? Progress { get; set; }
        public string Message { get; set; } = "Queued.";
        public object? Result { get; set; }
        public bool ResultWasSet { get; set; }
        public bool ResultExpired { get; set; }
    }
}

public sealed class McpJobContext
{
    private readonly Action<double?, string?> _report;
    private readonly Action<object?> _setResult;
    internal McpJobContext(CancellationToken cancellationToken, Action<double?, string?> report, Action<object?> setResult)
    {
        CancellationToken = cancellationToken;
        _report = report;
        _setResult = setResult;
    }

    public CancellationToken CancellationToken { get; }
    public void ReportProgress(double progress, string? message = null) => _report(Math.Max(0, Math.Min(1, progress)), message);
    public void ReportMessage(string message) => _report(null, message);
    public void SetResult(object? result) => _setResult(result);
}

public enum McpJobState { Queued, Running, Cancelling, Completed, Cancelled, Failed }

public sealed record McpJobSnapshot(
    string JobId,
    string ToolName,
    string? ParentOperationId,
    McpJobState State,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    double? Progress,
    string Message,
    int QueuePosition,
    TimeSpan? Elapsed,
    bool ResultExpired,
    object? Result);
