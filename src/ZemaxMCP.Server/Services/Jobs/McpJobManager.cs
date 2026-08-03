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
    private readonly object _gate = new();
    private readonly Queue<JobEntry> _pending = new();
    private readonly Dictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private bool _processorRunning;
    private bool _disposed;

    public event Action<McpJobSnapshot>? JobChanged;

    public McpJobSnapshot Enqueue(string toolName, Func<McpJobContext, Task> operation, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("A tool name is required.", nameof(toolName));
        if (operation == null) throw new ArgumentNullException(nameof(operation));

        JobEntry entry;
        McpJobSnapshot snapshot;
        lock (_gate)
        {
            ThrowIfDisposed();
            entry = new JobEntry(Guid.NewGuid().ToString("N"), toolName, operation, timeout);
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
                await entry.Operation(new McpJobContext(
                    token,
                    (progress, message) => PublishProgress(entry, progress, message),
                    result => PublishResult(entry, result))).ConfigureAwait(false);
                lock (_gate)
                {
                    entry.CompletedAt = DateTimeOffset.UtcNow;
                    entry.State = entry.Cancellation.IsCancellationRequested ? McpJobState.Cancelled : McpJobState.Completed;
                    entry.Message = entry.Cancellation.IsCancellationRequested ? "Cancelled." : "Completed.";
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
            Publish(Snapshot(entry));
        }
    }

    private void PublishProgress(JobEntry entry, double? progress, string? message)
    {
        lock (_gate)
        {
            entry.Progress = progress;
            if (!string.IsNullOrWhiteSpace(message)) entry.Message = message!;
        }
        Publish(Snapshot(entry));
    }

    private void PublishResult(JobEntry entry, object? result)
    {
        lock (_gate) entry.Result = result;
        Publish(Snapshot(entry));
    }

    private McpJobSnapshot Snapshot(JobEntry entry) => new(
        entry.Id, entry.ToolName, entry.State, entry.QueuedAt, entry.StartedAt, entry.CompletedAt,
        entry.Progress, entry.Message, QueuePosition(entry), entry.StartedAt == null ? null : DateTimeOffset.UtcNow - entry.StartedAt.Value, entry.Result);

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
            foreach (var entry in _jobs.Values) entry.Cancellation.Cancel();
        }
    }

    private sealed class JobEntry
    {
        public JobEntry(string id, string toolName, Func<McpJobContext, Task> operation, TimeSpan? timeout)
        {
            Id = id;
            ToolName = toolName;
            Operation = operation;
            Timeout = timeout;
        }

        public string Id { get; }
        public string ToolName { get; }
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
    McpJobState State,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    double? Progress,
    string Message,
    int QueuePosition,
    TimeSpan? Elapsed,
    object? Result);
