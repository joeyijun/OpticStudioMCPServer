using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ZemaxMCP.Rpc;

/// <summary>
/// Versioned, private Host-to-Worker contract.  This is deliberately not
/// JSON-RPC: MCP is terminated at the Host and never crosses this boundary.
/// Messages are framed as one UTF-8 JSON document per named-pipe line.
/// </summary>
public static class ZemaxRpcProtocol
{
    public const int Version = 1;
    public const string Hello = "hello";
    public const string HelloAccepted = "hello-accepted";
    public const string GetToolCatalog = "get-tool-catalog";
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

public sealed class ZemaxRpcError
{
    public string Code { get; set; } = "worker_error";
    public string Message { get; set; } = string.Empty;
    public bool IsTransient { get; set; }
}

public sealed class WorkerHello
{
    public int HostProcessId { get; set; }
    public int WorkerProcessId { get; set; }
    public string Secret { get; set; } = string.Empty;
}

public sealed class ToolCatalogRequest
{
    public string Toolset { get; set; } = "full-expert";
    public bool ReadOnly { get; set; }
}

public sealed class ToolInvocationRequest
{
    public string Command { get; set; } = string.Empty;
    public JsonElement Arguments { get; set; }
    public bool ReadOnly { get; set; }
    public string Toolset { get; set; } = "full-expert";
}

public sealed class ToolInvocationResult
{
    public JsonElement Result { get; set; }
    public bool IsError { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class WorkerStatus
{
    public bool ZosApiLoaded { get; set; }
    public bool Connected { get; set; }
    public string ConnectionMode { get; set; } = "unknown";
    public string? ZosApiAssembly { get; set; }
    public string? OpticStudioDataDirectory { get; set; }
    public string? LicenseStatus { get; set; }
    public string? SnapshotDirectory { get; set; }
    public string? LastSnapshotPath { get; set; }
    public IReadOnlyList<WorkerJobStatus> Jobs { get; set; } = Array.Empty<WorkerJobStatus>();
}

public sealed class WorkerJobStatus
{
    public string JobId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public double? Fraction { get; set; }
    public int QueuePosition { get; set; }
    public string? Message { get; set; }
}

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
