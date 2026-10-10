using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

/// <summary>Network-only MCP diagnostics. No WPF access, COM or model
/// modifications. Kept separate from the visual state and task controls.</summary>
internal static class McpDiagnosticsClient
{
    internal static string CheckConnectionHealth(string endpoint, string accessToken)
    {
        var healthEndpoint = endpoint.TrimEnd('/') + "/health";
        var request = (HttpWebRequest)WebRequest.Create(healthEndpoint);
        request.Method = "GET";
        request.Accept = "application/json";
        request.Timeout = 105000; // Cold Worker/ZOS-API bootstrap may legitimately take up to 90s.
        AddAuthorization(request, accessToken);
        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream());
        var result = JObject.Parse(reader.ReadToEnd());
        var bridge = result["bridgeRunning"]?.Value<bool>() == true;
        var worker = result["mcpServerRunning"]?.Value<bool>() == true;
        var zos = result["zosApiConnected"]?.Value<bool>() == true;
        var loaded = result["zosApiLoaded"]?.Value<bool>() == true;
        var licensed = result["licenseValidForApi"]?.Value<bool?>();
        var busy = result["workerBusy"]?.Value<bool>() == true;
        var fresh = result["statusFresh"]?.Value<bool>() ?? true;
        var age = result["statusAgeSeconds"]?.Value<double?>();
        if (!bridge) throw new InvalidOperationException("Host is reachable but reports bridgeRunning=false.");
        return "Connection check — Host: reachable; authentication: accepted; Worker: " +
            (worker ? "running" : "unavailable") + "; ZOS-API: " +
            (loaded ? "loaded" : "not loaded") + "; OpticStudio: " +
            (zos ? "connected" : "disconnected") + "; license: " +
            (result["licenseStatus"]?.ToString() ?? "not reported") +
            "; API license valid: " + (licensed.HasValue ? licensed.Value.ToString() : "not reported") +
            (busy ? "; Worker busy (status " + (fresh ? "fresh" : "last known") +
                (age.HasValue ? ", verified " + Math.Round(age.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s ago" : ", never verified") + ")" :
                (fresh ? "" : "; Worker status unavailable")) +
            ". This is a health check, not a tool execution test.";
    }

    internal static JObject SendMcpJsonRpc(string endpoint, string accessToken,
        string method, JObject parameters, string? routingName = null)
    {
        // 2026-07-28 stateless MCP transport: no optical edits.
        var meta = new JObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JObject
            {
                ["name"] = "zemax-launcher",
                ["version"] = typeof(McpDiagnosticsClient).Assembly.GetName().Version?.ToString(3) ?? "unknown"
            },
            ["io.modelcontextprotocol/clientCapabilities"] = new JObject
            {
                ["extensions"] = new JObject { ["io.modelcontextprotocol/tasks"] = new JObject() }
            }
        };
        parameters["_meta"] = meta;
        var message = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2741,
            ["method"] = method,
            ["params"] = parameters
        };
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "application/json, text/event-stream";
        request.Timeout = method == "tools/call" ? 105000 : 20000;
        request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        request.Headers["Mcp-Method"] = method;
        if (!string.IsNullOrEmpty(routingName)) request.Headers["Mcp-Name"] = routingName;
        AddAuthorization(request, accessToken);
        var bytes = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None));
        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream());
        var raw = reader.ReadToEnd();
        if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            raw = SelectMcpSseResponse(raw, message["id"]!).ToString(Newtonsoft.Json.Formatting.None);
        }
        var rpc = JObject.Parse(raw);
        if (rpc["error"] is JToken error)
            throw new InvalidOperationException("MCP " + method + ": " +
                (error["message"]?.ToString() ?? "JSON-RPC error"));
        return rpc;
    }

    private static JObject SelectMcpSseResponse(string raw, JToken requestId)
    {
        foreach (var frame in raw.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var data = string.Join("\n", frame.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line.Substring(5).TrimStart()));
            if (string.IsNullOrWhiteSpace(data)) continue;
            var candidate = JObject.Parse(data);
            if (candidate["method"] == null && JToken.DeepEquals(candidate["id"], requestId) &&
                (candidate["result"] != null || candidate["error"] != null)) return candidate;
        }
        throw new InvalidDataException("MCP SSE contained no response matching the request ID.");
    }

    internal static string TestMcpFunctionality(string endpoint, string accessToken)
    {
        var list = SendMcpJsonRpc(endpoint, accessToken, "tools/list", new JObject());
        var tools = list["result"]?["tools"] as JArray ??
            throw new InvalidDataException("tools/list returned no tool array.");
        if (tools.Count == 0 || !tools.Any(t => t["name"]?.ToString() == "zemax_status"))
            throw new InvalidDataException("MCP has no discoverable read-only zemax_status tool.");
        var status = SendMcpJsonRpc(endpoint, accessToken, "tools/call",
            new JObject { ["name"] = "zemax_status", ["arguments"] = new JObject() }, "zemax_status");
        if (status["result"]?["isError"]?.Value<bool>() == true ||
            !(status["result"]?["content"] is JArray content) || content.Count == 0)
            throw new InvalidDataException("Actual MCP zemax_status call returned no successful tool result.");

        // Modern discovery is stateless; initialize belongs to legacy protocols.
        // No optical Task is started by this capability probe.
        var discovery = SendMcpJsonRpc(endpoint, accessToken, "server/discover", new JObject());
        var tasks = discovery["result"]?["capabilities"]?["extensions"]?["io.modelcontextprotocol/tasks"] != null;
        return "MCP functional test PASS — tools/list: " + tools.Count +
            " tools; real read-only zemax_status: result returned; 2026-07-28 server/discover: success; " +
            "official Tasks advertised: " + (tasks ? "yes" : "no") +
            ". A completed Task result requires an owned Task ID in the Tasks page; this test does not start an optical Job.";
    }


    private static void AddAuthorization(HttpWebRequest request,string accessToken)
    {
        if(!string.IsNullOrWhiteSpace(accessToken))
            request.Headers[HttpRequestHeader.Authorization]="Bearer "+accessToken;
    }
}
