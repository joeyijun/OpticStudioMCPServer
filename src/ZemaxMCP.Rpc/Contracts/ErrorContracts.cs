namespace ZemaxMCP.Rpc;

public sealed class ZemaxRpcError
{
    public string Code { get; set; } = "worker_error";
    public string Message { get; set; } = string.Empty;
    public bool IsTransient { get; set; }
}
