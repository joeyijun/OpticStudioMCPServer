using Microsoft.AspNetCore.Http;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Minimal browser boundary for the MCP endpoint. Native MCP clients do not
/// send Origin and remain supported; browser origins are never wildcarded.
/// </summary>
internal static class OriginPolicy
{
    private const string AllowedHeaders = "Authorization, Content-Type, Accept, MCP-Protocol-Version, Mcp-Method, Mcp-Name, Mcp-Session-Id";

    public static bool TryApply(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !IsAllowed(originUri, context.Request.Host))
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

    internal static bool IsAllowed(Uri origin, HostString requestHost)
    {
        var endpointHost = requestHost.Host;
        if (string.IsNullOrWhiteSpace(endpointHost)) return false;
        if (string.Equals(origin.Host, endpointHost, StringComparison.OrdinalIgnoreCase)) return true;
        return IsLoopback(origin.Host) && IsLoopback(endpointHost);
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);
}
