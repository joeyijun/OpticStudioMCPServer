using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace ZemaxMCP.PrivateRpcTests;

internal static partial class Program
{
    private static async Task VerifyQueueHttpBoundariesAsync()
    {
        var root = FindRepositoryRoot();
        var executable = Path.Combine(root, "src", "ZemaxMCP.HttpBridge", "bin", "Release", "net10.0-windows", "ZemaxMCP.Host.exe");
        var directory = Path.Combine(Path.GetTempPath(), "ZemaxMCP-queue-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = ReserveLoopbackPort();
        var info = new ProcessStartInfo(executable, $"--worker \"{Environment.ProcessPath}\" --port {port} --log-dir \"{directory}\" --max-pending-requests 1 --queue-wait-timeout-seconds 1")
        { UseShellExecute = false, CreateNoWindow = true };
        info.Environment["ZEMAX_MCP_TOKEN"] = "private-rpc-e2e-token";
        using var process = Process.Start(info)!;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var ready = false;
            for (var i = 0; i < 100; i++)
            {
                try { using var list = await Send2026ListToolsAsync(client, endpoint, 700, "queue-test", "queue-1"); if (list.IsSuccessStatusCode) { ready = true; break; } }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            if (!ready) throw new Exception("Queue test Host failed to start.");
            async Task<JsonElement> HealthAsync()
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health");
                request.Headers.Authorization = new("Bearer", "private-rpc-e2e-token");
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return json.RootElement.Clone();
            }
            var hold = Send2026ToolCallAsync(client, endpoint, 701, "zemax_get_system", "queue-test", "queue-1");
            var active = false;
            for (var i = 0; i < 100; i++)
            {
                if ((await HealthAsync()).GetProperty("activeRequests").GetInt32() > 0) { active = true; break; }
                await Task.Delay(25);
            }
            if (!active) throw new Exception("Fake optical hold did not become active.");
            var queued = Send2026ToolCallAsync(client, endpoint, 702, "zemax_status", "queue-test", "queue-1");
            var sawQueue = false;
            for (var i = 0; i < 30; i++)
            {
                if ((await HealthAsync()).GetProperty("requestQueue").GetProperty("queued").GetInt32() == 1) { sawQueue = true; break; }
                await Task.Delay(10);
            }
            if (!sawQueue) throw new Exception("Host did not report the queued request.");
            using var full = await Send2026ToolCallAsync(client, endpoint, 703, "zemax_status", "queue-test", "queue-1");
            AssertQueueError(await ReadFirstMcpPayloadAsync(full), 703, "queue_full");
            using var timeout = await queued;
            AssertQueueError(await ReadFirstMcpPayloadAsync(timeout), 702, "queue_timeout");
            using var held = await hold;
            if (!(await ReadFirstMcpPayloadAsync(held)).Contains("echo-ok")) throw new Exception("Queue rejection interrupted active operation.");
            using var after = await Send2026ToolCallAsync(client, endpoint, 704, "zemax_status", "queue-test", "queue-1");
            if (!(await ReadFirstMcpPayloadAsync(after)).Contains("echo-ok")) throw new Exception("Host queue did not recover after rejection.");
            Console.WriteLine("HTTP/SSE queue full and timeout preserve original request IDs; active and subsequent calls remain functional.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            for (var i = 0; i < 20; i++)
                try { Directory.Delete(directory, true); break; } catch (IOException) when (i < 19) { await Task.Delay(100); }
        }
    }
    private static void AssertQueueError(string payload, int id, string code)
    {
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.GetProperty("id").GetInt32() != id ||
            !json.RootElement.GetProperty("result").GetProperty("isError").GetBoolean() || !payload.Contains(code))
            throw new Exception("Queue error lost code or original request ID: " + payload);
    }
}
