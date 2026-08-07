using Microsoft.AspNetCore.Http;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Minimal browser boundary for the MCP endpoint. Native MCP clients do not
/// send Origin and remain supported; browser origins are never wildcarded.
/// </summary>
internal static class OriginPolicy
{
    private const string AllowedHeaders = "Authorization, Content-Type, Accept, MCP-Protocol-Version, Mcp-Method, Mcp-Name, Mcp-Version, Mcp-Session-Id";

    public static bool TryApply(HttpContext context, IReadOnlyCollection<OriginRule> allowedOrigins)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !IsAllowed(originUri, allowedOrigins))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return false;
        }

        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.Append("Vary", "Origin");
        context.Response.Headers.AccessControlAllowMethods = "GET, POST, DELETE, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = AllowedHeaders;
        return true;
    }

    internal static bool IsAllowed(Uri origin, IReadOnlyCollection<OriginRule> allowedOrigins) =>
        allowedOrigins.Any(rule => rule.Matches(origin));
}
