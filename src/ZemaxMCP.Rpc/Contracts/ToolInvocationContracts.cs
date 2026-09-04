using System.Text.Json;

namespace ZemaxMCP.Rpc;

/// <summary>Manifest-constrained tool invocation crossing the private RPC boundary.</summary>
public sealed class ToolInvocationRequest
{
    public string Command { get; set; } = string.Empty;
    public JsonElement Arguments { get; set; }
    public bool ReadOnly { get; set; }
    public string Toolset { get; set; } = "full-expert";
}
