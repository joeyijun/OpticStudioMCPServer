namespace ZemaxMCP.HttpBridge.ModernHost;

internal sealed class RequestAdmissionException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

// Bounds waiting BEFORE the lease/RPC locks, not only after reaching COM.
// Diagnostic endpoints and protocol cancellation do not enter this queue.
internal sealed class HostRequestAdmission(int capacity, TimeSpan waitTimeout) : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _execution = new(1, 1);
    private int _queued, _active;
    private long _full, _timeouts;
    private bool _disposed;
    private bool _frozen;
    public bool TryFreezeIfIdle()
    {
        lock (_sync)
        {
            if (_active != 0 || _queued != 0 || _disposed) return false;
            _frozen = true;
            return true;
        }
    }
    public void Resume() { lock (_sync) _frozen = false; }
    public int CurrentCount => _execution.CurrentCount;

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frozen) throw new RequestAdmissionException("service_maintenance", "The local service is prepared for a safe update. New optical calls are paused.");
            if (_execution.Wait(0)) { _active++; return new Lease(this); }
            if (_queued >= capacity)
            {
                _full++;
                throw new RequestAdmissionException("queue_full", "The Host tool queue is full. Retry after an existing operation completes.");
            }
            _queued++;
        }
        var acquired = false;
        try
        {
            acquired = await _execution.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                lock (_sync) _timeouts++;
                throw new RequestAdmissionException("queue_timeout", "The Host queue wait deadline expired; no Worker operation was started.");
            }
            lock (_sync)
            {
                if (_disposed) { _execution.Release(); throw new ObjectDisposedException(nameof(HostRequestAdmission)); }
                _active++;
            }
            return new Lease(this);
        }
        finally { lock (_sync) _queued--; }
    }

    public object GetHealth()
    {
        lock (_sync) return new { capacity, queued = _queued, active = _active,
            waitTimeoutSeconds = waitTimeout.TotalSeconds, rejectedFull = _full, rejectedTimeout = _timeouts };
    }

    private void Release() { lock (_sync) { _active--; _execution.Release(); } }
    public void Dispose() { lock (_sync) _disposed = true; }
    private sealed class Lease(HostRequestAdmission owner) : IDisposable
    {
        private HostRequestAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
