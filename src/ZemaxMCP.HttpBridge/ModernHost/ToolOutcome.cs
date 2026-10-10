using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Typed error semantics without changing legacy *successful* optical DTOs.
/// Preserve existing JSON fields on a legacy failed result, adding a stable
/// machine-readable code. The MCP isError flag remains authoritative.
/// </summary>
internal static class ToolOutcome
{
    internal const string DomainError = "domain_error";
    internal const string InvalidArgument = "invalid_argument";
    internal const string NotFound = "not_found";
    internal const string Conflict = "conflict";
    internal const string Expired = "expired";
    internal const string TransportError = "transport_error";

    internal static CallToolResult Failure(string code, string message) =>
        new()
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new { success = false, code, message })
                }
            }
        };

    internal static CallToolResult Normalize(CallToolResult result)
    {
        if (result.IsError != true) return result;
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "Tool failed.";
        var code = Classify(text);
        try
        {
            if (JsonNode.Parse(text) is JsonObject details)
            {
                // Do not replace legacy error fields or turn an unsuccessful
                // tool into success. Just provide stable outcome metadata.
                if (details["code"] == null) details["code"] = code;
                if (details["success"] == null) details["success"] = false;
                if (details["message"] == null)
                    details["message"] = details["error"]?.ToString() ?? "Tool failed.";
                return FailureJson(details.ToJsonString());
            }
        }
        catch (JsonException) { }
        return Failure(code, text);
    }

    private static CallToolResult FailureJson(string text) =>
        new()
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
        };

    internal static string Classify(string error)
    {
        if (error.Contains("expired", StringComparison.OrdinalIgnoreCase)) return Expired;
        if (error.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            error.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)) return NotFound;
        if (error.Contains("currently leased", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("background Job is active", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("at capacity", StringComparison.OrdinalIgnoreCase)) return Conflict;
        if (error.Contains("private pipe", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("RPC transport", StringComparison.OrdinalIgnoreCase))
            return TransportError;
        return DomainError;
    }
}
