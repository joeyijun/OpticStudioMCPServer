namespace ZemaxMCP.HttpBridge.ModernHost;

internal sealed class McpActivityMonitor
{
    private readonly object _sync = new();
    private string _lastClient = "None yet";
    private string? _lastTool;
    private DateTimeOffset? _lastRequestAt;
    private readonly Dictionary<long, McpActiveOperation> _activeOperations = new();
    private long _nextOperationId;

    public IDisposable Begin(string client, string tool)
    {
        long operationId;
        lock (_sync)
        {
            operationId = ++_nextOperationId;
            _lastClient = client;
            _lastTool = tool;
            _lastRequestAt = DateTimeOffset.UtcNow;
            _activeOperations.Add(operationId, new McpActiveOperation(client, tool, _lastRequestAt.Value));
        }
        return new Releaser(this, operationId);
    }

    public McpActivitySnapshot GetHealth()
    {
        lock (_sync)
            return new McpActivitySnapshot(_lastClient, _lastTool, _lastRequestAt,
                _activeOperations.Values.OrderBy(operation => operation.StartedAt).ToArray());
    }

    private void End(long operationId)
    {
        lock (_sync)
        {
            if (_activeOperations.Remove(operationId, out var operation))
            {
                _lastClient = operation.Client;
                _lastTool = operation.Tool;
                _lastRequestAt = DateTimeOffset.UtcNow;
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private McpActivityMonitor? _monitor;
        private readonly long _operationId;
        public Releaser(McpActivityMonitor monitor, long operationId)
        {
            _monitor = monitor;
            _operationId = operationId;
        }
        public void Dispose() => Interlocked.Exchange(ref _monitor, null)?.End(_operationId);
    }
}

internal sealed record McpActiveOperation(string Client, string Tool, DateTimeOffset StartedAt);

internal sealed record McpActivitySnapshot(string LastClient, string? LastTool, DateTimeOffset? LastRequestAt,
    IReadOnlyList<McpActiveOperation> ActiveOperations)
{
    public int ActiveRequests => ActiveOperations.Count;
    public DateTimeOffset? ActiveSince => ActiveOperations.Count == 0 ? null : ActiveOperations[0].StartedAt;
}
