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
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(15);
    private string? _ownerClientId;
    private DateTimeOffset _lastActivity;
    private string? _activeOperation;

    public async Task<IDisposable> AcquireAsync(string clientId, string operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) clientId = "anonymous";
        lock (_sync)
        {
            var expired = _ownerClientId != null && DateTimeOffset.UtcNow - _lastActivity > _idleTimeout && _activeOperation == null;
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

    public object GetHealth()
    {
        lock (_sync) return new
        {
            owner = _ownerClientId,
            activeOperation = _activeOperation,
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

    private sealed class Releaser : IDisposable
    {
        private OpticStudioControlLease? _lease;
        private readonly string _clientId;
        public Releaser(OpticStudioControlLease lease, string clientId) { _lease = lease; _clientId = clientId; }
        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Release(_clientId);
    }
}
