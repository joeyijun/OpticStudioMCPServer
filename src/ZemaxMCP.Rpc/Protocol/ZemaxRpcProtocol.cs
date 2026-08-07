using System.Text.Json;

namespace ZemaxMCP.Rpc;

/// <summary>
/// Versioned private Host-to-Worker protocol. MCP terminates at the Host and
/// never crosses this boundary. Frames are one UTF-8 JSON document per pipe line.
/// </summary>
public static class ZemaxRpcProtocol
{
    // v3 removes Worker-owned discovery and requires manifest fingerprint
    // negotiation during the private-pipe startup handshake.
    public const int Version = 3;
    public const string InvokeTool = "invoke-tool";
    public const string CancelOperation = "cancel-operation";
    public const string GetStatus = "get-status";
    public const string Progress = "progress";
    public const string SnapshotCreated = "snapshot-created";
    public const string Result = "result";
    public const string Error = "error";
}

public sealed class ZemaxRpcEnvelope
{
    public int Version { get; set; } = ZemaxRpcProtocol.Version;
    public string Kind { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}

public sealed class WorkerHandshake
{
    public int RpcVersion { get; set; } = ZemaxRpcProtocol.Version;
    public int WorkerProcessId { get; set; }
    public string Secret { get; set; } = string.Empty;
    public string ManifestFingerprint { get; set; } = string.Empty;
}

public sealed class WorkerHandshakeAck
{
    public int RpcVersion { get; set; } = ZemaxRpcProtocol.Version;
    public bool Accepted { get; set; }
    public string ManifestFingerprint { get; set; } = string.Empty;
    public string? Error { get; set; }
}
