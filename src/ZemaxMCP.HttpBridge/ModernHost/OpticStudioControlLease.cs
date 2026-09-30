using ZemaxMCP.Rpc;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Exclusive ownership of the stateful OpticStudio instance. This is separate
/// from the MCP transport: modern MCP requests may be stateless while a lens
/// system must still have one intentional controller.
/// </summary>
internal sealed class OpticStudioControlLease
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly TimeSpan _idleTimeout;
    private readonly Dictionary<string, JobHold> _jobHolds = new(StringComparer.Ordinal);
    private string? _ownerClientId;
    private DateTimeOffset _lastActivity;
    private string? _activeOperation;

    public OpticStudioControlLease(TimeSpan? idleTimeout = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(15);
        if (_idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
    }

    public async Task<IDisposable> AcquireAsync(string clientId, string operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) clientId = "anonymous";
        lock (_sync)
        {
            var expired = _ownerClientId != null && DateTimeOffset.UtcNow - _lastActivity > _idleTimeout &&
                          _activeOperation == null && _jobHolds.Count == 0;
            if (expired) _ownerClientId = null;
            if (_ownerClientId != null && !string.Equals(_ownerClientId, clientId, StringComparison.Ordinal))
                throw new InvalidOperationException("OpticStudio control is currently leased to another MCP client.");
            _ownerClientId = clientId;
            _lastActivity = DateTimeOffset.UtcNow;
        }

        await _execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _activeOperation = operation;
            _lastActivity = DateTimeOffset.UtcNow;
        }
        return new Releaser(this, clientId);
    }

    public bool ReleaseOwnership(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        lock (_sync)
        {
            if (_activeOperation != null || _jobHolds.Count != 0 ||
                !string.Equals(_ownerClientId, clientId, StringComparison.Ordinal))
                return false;

            _ownerClientId = null;
            _lastActivity = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool RetainForJob(string clientId, string jobId, long generation)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(jobId) || generation <= 0) return false;
        lock (_sync)
        {
            if (!string.Equals(_ownerClientId, clientId, StringComparison.Ordinal)) return false;
            _jobHolds[jobId] = new JobHold(clientId, generation);
            _lastActivity = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void ObserveJob(long generation, WorkerJobStatus job)
    {
        if (generation <= 0 || string.IsNullOrWhiteSpace(job.JobId)) return;
        if (!IsTerminal(job.State)) return;
        lock (_sync)
        {
            if (_jobHolds.TryGetValue(job.JobId, out var hold) && hold.Generation == generation)
            {
                _jobHolds.Remove(job.JobId);
                _lastActivity = DateTimeOffset.UtcNow;
            }
        }
    }

    public void ReleaseGeneration(long generation)
    {
        if (generation <= 0) return;
        lock (_sync)
        {
            foreach (var jobId in _jobHolds.Where(pair => pair.Value.Generation == generation).Select(pair => pair.Key).ToArray())
                _jobHolds.Remove(jobId);
            _lastActivity = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsTerminal(string? state) =>
        string.Equals(state, "Completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);

    public object GetHealth()
    {
        lock (_sync) return new
        {
            owner = _ownerClientId,
            activeOperation = _activeOperation,
            backgroundJobs = _jobHolds.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            backgroundJobCount = _jobHolds.Count,
            lastActivity = _lastActivity == default ? (DateTimeOffset?)null : _lastActivity,
            idleTimeoutSeconds = (int)_idleTimeout.TotalSeconds
        };
    }

    private void Release(string clientId)
    {
        lock (_sync)
        {
            if (string.Equals(_ownerClientId, clientId, StringComparison.Ordinal))
            {
                _activeOperation = null;
                _lastActivity = DateTimeOffset.UtcNow;
            }
        }
        _execution.Release();
    }

    private sealed record JobHold(string ClientId, long Generation);

    private sealed class Releaser : IDisposable
    {
        private OpticStudioControlLease? _lease;
        private readonly string _clientId;
        public Releaser(OpticStudioControlLease lease, string clientId) { _lease = lease; _clientId = clientId; }
        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Release(_clientId);
    }
}
