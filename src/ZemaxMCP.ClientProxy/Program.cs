using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.ClientProxy;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "logs", "client-proxy-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

    private static int Main(string[] args)
    {
        try { RunAsync(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Log("Fatal: " + ex); return 1; }
    }

    private static async Task RunAsync(string[] args)
    {
        var endpoint = ReadEndpoint(args);
        var accessToken = ReadOption(args, "--token");
        if (string.IsNullOrWhiteSpace(accessToken)) accessToken = Environment.GetEnvironmentVariable("ZEMAX_MCP_TOKEN") ?? "";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        string? sessionId = null;
        Log("Started local stdio proxy for " + endpoint);

        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject? request = null;
            try
            {
                request = JObject.Parse(line);
                using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                if (!string.IsNullOrWhiteSpace(accessToken)) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                if (!string.IsNullOrWhiteSpace(sessionId)) message.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
                message.Content = new StringContent(line, Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(message).ConfigureAwait(false);
                if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) sessionId = sessions.FirstOrDefault() ?? sessionId;
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    if (!string.IsNullOrWhiteSpace(body)) await Console.Out.WriteLineAsync(body).ConfigureAwait(false);
                }
                else
                {
                    await WriteErrorAsync(request["id"], -32002, "Zemax MCP endpoint returned HTTP " + (int)response.StatusCode + ".").ConfigureAwait(false);
                    Log("HTTP " + (int)response.StatusCode + " for " + (request["method"]?.ToString() ?? "unknown"));
                }
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(request?["id"], -32003, "Could not reach the Zemax MCP endpoint: " + ex.Message).ConfigureAwait(false);
                Log("Request failed: " + ex);
            }
        }
    }

    private static Uri ReadEndpoint(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--url", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(args[i + 1], UriKind.Absolute, out var endpoint) &&
                (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)) return endpoint;
        }
        throw new ArgumentException("Usage: ZemaxMCP.ClientProxy.exe --url http://host:port/mcp");
    }

    private static string ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return "";
    }

    private static async Task WriteErrorAsync(JToken? id, int code, string message)
    {
        // Notifications do not have an id and must not receive a JSON-RPC response.
        if (id == null) return;
        var response = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
        await Console.Out.WriteLineAsync(response.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath, DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { }
    }
}
