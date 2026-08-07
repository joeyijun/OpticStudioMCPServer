namespace ZemaxMCP.Rpc;

public sealed class SnapshotCreatedEvent
{
    public string Path { get; set; } = string.Empty;
}

public sealed class OperationProgress
{
    public string OperationId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public double? Fraction { get; set; }
    public int QueuePosition { get; set; }
    public string State { get; set; } = string.Empty;
    public string? Message { get; set; }
}
