namespace ZemaxMCP.HttpBridge.ModernHost;

internal sealed class McpActivityMonitor
{
    private readonly object _sync = new();
    private string _lastClient = "None yet";
    private string? _lastTool;
    private DateTimeOffset? _lastRequestAt;
    private readonly Dictionary<long, McpActiveOperation> _activeOperations = new();
    private long _nextOperationId;
    private long _eventSequence;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    // Only the latest sequence for up to 256 owners is retained. Events are
    // not persisted, and cursors do not encode other clients' activity counts.
    private readonly Dictionary<string, long> _ownerEvents = new(StringComparer.Ordinal);

    public IDisposable Begin(string client, string tool)
    {
        long operationId;
        lock (_sync)
        {
            operationId = ++_nextOperationId;
            TouchOwnerLocked(client);
            _lastClient = client;
            _lastTool = tool;
            _lastRequestAt = DateTimeOffset.UtcNow;
            _activeOperations.Add(operationId, new McpActiveOperation(client, tool, _lastRequestAt.Value));
        }
        return new Releaser(this, operationId);
    }

    private void TouchOwnerLocked(string client)
    {
        if (_ownerEvents.Count >= 256 && !_ownerEvents.ContainsKey(client))
        {
            var evict = _ownerEvents.OrderBy(x => x.Value)
                .FirstOrDefault(x => !_activeOperations.Values.Any(a => a.Client == x.Key));
            if (evict.Key != null) _ownerEvents.Remove(evict.Key);
            else _ownerEvents.Remove(_ownerEvents.OrderBy(x => x.Value).First().Key);
        }
        _ownerEvents[client] = ++_eventSequence;
    }

    // Diff cursors are opaque (not numeric sequence IDs). A scoped client
    // cannot infer that another client's operations happened between polls.
    private string CursorLocked(string owner, bool scoped)
    {
        var sequence = scoped ? _ownerEvents.GetValueOrDefault(owner) : _eventSequence;
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(_epoch + ":" + (scoped ? owner : "*") + ":" + sequence));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public McpActivityDelta GetDelta(string owner, string? previousCursor, bool scoped)
    {
        lock (_sync)
        {
            var cursor = CursorLocked(owner, scoped);
            var changed = !string.Equals(cursor, previousCursor, StringComparison.Ordinal);
            var snapshot = !changed ? null : scoped
                ? GetForClient(owner) : GetHealth();
            return new McpActivityDelta(cursor, changed, snapshot);
        }
    }

    public McpActivitySnapshot GetHealth()
    {
        lock (_sync)
            return new McpActivitySnapshot(_lastClient, _lastTool, _lastRequestAt,
                _activeOperations.Values.OrderBy(operation => operation.StartedAt).ToArray());
    }

    // Scoped credentials must not discover another client's tool names or
    // activity timestamps through /activity or /health diagnostics.
    public McpActivitySnapshot GetForClient(string clientId)
    {
        lock (_sync)
        {
            var mine = _activeOperations.Values
                .Where(operation => string.Equals(operation.Client, clientId, StringComparison.Ordinal))
                .OrderBy(operation => operation.StartedAt).ToArray();
            var ownsLast = string.Equals(_lastClient, clientId, StringComparison.Ordinal);
            return new McpActivitySnapshot(
                ownsLast ? _lastClient : "None yet",
                ownsLast ? _lastTool : null,
                ownsLast ? _lastRequestAt : null, mine);
        }
    }

    private void End(long operationId)
    {
        lock (_sync)
        {
            if (_activeOperations.Remove(operationId, out var operation))
            {
                TouchOwnerLocked(operation.Client);
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

internal sealed record McpActivityDelta(string Cursor, bool Changed, McpActivitySnapshot? Activity);
