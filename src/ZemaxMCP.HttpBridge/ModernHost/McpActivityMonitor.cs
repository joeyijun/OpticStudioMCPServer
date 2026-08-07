namespace ZemaxMCP.HttpBridge.ModernHost;

internal sealed class McpActivityMonitor
{
    private readonly object _sync = new();
    private string _lastClient = "None yet";
    private string? _lastTool;
    private DateTimeOffset? _lastRequestAt;
    private DateTimeOffset? _activeSince;
    private int _activeRequests;

    public IDisposable Begin(string client, string tool)
    {
        lock (_sync)
        {
            _lastClient = client;
            _lastTool = tool;
            _lastRequestAt = DateTimeOffset.UtcNow;
            _activeSince ??= _lastRequestAt;
            _activeRequests++;
        }
        return new Releaser(this);
    }

    public McpActivitySnapshot GetHealth()
    {
        lock (_sync) return new McpActivitySnapshot(_lastClient, _lastTool, _lastRequestAt, _activeSince, _activeRequests);
    }

    private void End()
    {
        lock (_sync)
        {
            _activeRequests = Math.Max(0, _activeRequests - 1);
            if (_activeRequests == 0) _activeSince = null;
        }
    }

    private sealed class Releaser : IDisposable
    {
        private McpActivityMonitor? _monitor;
        public Releaser(McpActivityMonitor monitor) => _monitor = monitor;
        public void Dispose() => Interlocked.Exchange(ref _monitor, null)?.End();
    }
}

internal sealed record McpActivitySnapshot(string LastClient, string? LastTool, DateTimeOffset? LastRequestAt, DateTimeOffset? ActiveSince, int ActiveRequests);
