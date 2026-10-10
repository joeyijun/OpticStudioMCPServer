using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

/// <summary>Stateless, owner-authorized MCP Job transport. No WPF dependencies:
/// caller supplies endpoint + identity. Job-result ID is always revalidated.</summary>
internal static class JobActionClient
{
    internal static string RequestJobCancellation(string endpoint, string accessToken, string jobId)
    {
        // Never turn a missing/finished Job into an optimistic "accepted".
        // Preserve the actual Worker-confirmed state for the UI.
        var response = RequestJobTool(endpoint, accessToken, jobId, "zemax_job_cancel");
        var current = JObject.Parse(response);
        var state = current["state"]?.ToString() ?? "Unknown";
        return state switch
        {
            "Cancelling" or "Cancelled" =>
                "Cancellation reported by Worker for " + jobId + ": " + state +
                ". Refresh to confirm the final state.",
            "Completed" or "Failed" =>
                "Job " + jobId + " already reached terminal state " + state +
                "; no new cancellation was performed.",
            _ => "Worker Job " + jobId + " state after cancel attempt: " + state +
                "; verify status before concluding cancellation."
        };
    }

    private sealed class JobToolErrorException : InvalidOperationException
    {
        public string OutcomeCode { get; }
        public JobToolErrorException(string code, string message)
            : base("Job request [" + code + "]: " + message) => OutcomeCode = code;
    }

    internal static string RequestJobTool(string endpoint, string accessToken, string jobId, string toolName)
    {
        // Always route through ordinary MCP tools/call so scoped ownership and
        // the Worker's generation check remain authoritative. This is not an
        // elevated Launcher-specific administrative cancellation API.
        var body = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JObject
            {
                ["name"] = toolName,
                ["arguments"] = new JObject { ["jobId"] = jobId },
                ["_meta"] = new JObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientInfo"] = new JObject
                    {
                        ["name"] = "zemax-launcher",
                        ["version"] = typeof(JobActionClient).Assembly.GetName().Version?.ToString(3) ?? "unknown"
                    }
                }
            }
        };
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "application/json, text/event-stream";
        request.Timeout = 15000;
        request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        request.Headers["Mcp-Method"] = "tools/call";
        request.Headers["Mcp-Name"] = toolName;
        AddAuthorization(request, accessToken);
        var bytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream());
        var raw = reader.ReadToEnd();
        if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            raw = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))?.Substring(5) ?? "";
        }
        var rpc = JObject.Parse(raw);
        if (rpc["error"] != null || rpc["result"]?["isError"]?.Value<bool>() == true)
        {
            // New Hosts provide a machine-readable failure envelope. Legacy
            // Hosts still report plain errors; show their text without
            // conflating "not found", "expired" and "conflict".
            var errorText = rpc["result"]?["content"]?.FirstOrDefault()?["text"]?.ToString();
            if (!string.IsNullOrWhiteSpace(errorText))
            {
                try
                {
                    var error = JObject.Parse(errorText);
                    var code = error["code"]?.ToString() ?? "domain_error";
                    var explanation = error["message"]?.ToString() ?? "Request rejected.";
                    throw new JobToolErrorException(code, explanation);
                }
                catch (Newtonsoft.Json.JsonException) { }
            }
            throw new InvalidOperationException("MCP rejected the Job request: " +
                (rpc["error"]?["message"]?.ToString() ?? errorText ?? "Not found, not owned, or unavailable."));
        }
        var text = rpc["result"]?["content"]?.FirstOrDefault()?["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(text) || text == "null")
            throw new InvalidOperationException("Worker returned no matching Job. It may be expired or owned by another client.");
        var parsed = JObject.Parse(text);
        if (!string.Equals(parsed["jobId"]?.ToString(), jobId, StringComparison.Ordinal))
            throw new InvalidDataException("Job result ID does not match the requested Job.");
        return parsed.ToString(Newtonsoft.Json.Formatting.None);
    }


    private static void AddAuthorization(HttpWebRequest request,string token)
    {
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers[HttpRequestHeader.Authorization]="Bearer "+token;
    }
}
