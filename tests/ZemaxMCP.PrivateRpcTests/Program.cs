using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ZemaxMCP.HttpBridge.ModernHost;
using ZemaxMCP.Rpc;

namespace ZemaxMCP.PrivateRpcTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--pipe", StringComparison.OrdinalIgnoreCase))
        {
            await RunFakeWorkerAsync(args[1]).ConfigureAwait(false);
            return 0;
        }

        try
        {
            VerifyOriginBoundary();
            await VerifyPipeFaultRecoveryAsync().ConfigureAwait(false);
            await VerifyHardTimeoutRecoveryAsync().ConfigureAwait(false);
            await VerifyMcpHttpToWorkerEndToEndAsync().ConfigureAwait(false);
            Console.WriteLine("Private Host-to-Worker RPC recovery, Origin boundary, and MCP HTTP E2E verification passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void VerifyOriginBoundary()
    {
        var localRules = new[]
        {
            OriginRule.AnyPort("http", "127.0.0.1"),
            OriginRule.AnyPort("http", "localhost"),
            OriginRule.AnyPort("http", "::1")
        };
        if (!OriginPolicy.IsAllowed(new Uri("http://127.0.0.1:4567"), localRules) ||
            !OriginPolicy.IsAllowed(new Uri("http://localhost:4567"), localRules) ||
            OriginPolicy.IsAllowed(new Uri("https://attacker.example"), localRules))
            throw new InvalidOperationException("Origin allow-list did not enforce configured local origins.");
        var lanRules = new[] { OriginRule.Parse("http://192.168.8.20:3000") };
        if (!OriginPolicy.IsAllowed(new Uri("http://192.168.8.20:3000"), lanRules) ||
            OriginPolicy.IsAllowed(new Uri("http://192.168.8.20:3001"), lanRules))
            throw new InvalidOperationException("An explicit LAN Origin must not inherit a wildcard port.");
    }

    private static async Task VerifyPipeFaultRecoveryAsync()
    {
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", "exit-after-status");
        await using var client = new WorkerRpcClient(CreateOptions(10, 20));
        var first = await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (!first.Connected) throw new InvalidOperationException("Fake Worker status did not reach the Host.");
        await Task.Delay(150).ConfigureAwait(false);
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", null);
        var recovered = await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (!recovered.Connected) throw new InvalidOperationException("Host did not recreate the Worker after a pipe EOF.");
    }

    private static async Task VerifyHardTimeoutRecoveryAsync()
    {
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", "hang-status");
        await using var client = new WorkerRpcClient(CreateOptions(10, 20));
        var started = Stopwatch.StartNew();
        try
        {
            await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A hung Worker status request unexpectedly completed.");
        }
        catch (TimeoutException) { }
        if (started.Elapsed > TimeSpan.FromSeconds(25))
            throw new InvalidOperationException("Hard Worker recovery exceeded its bounded timeout.");

        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", null);
        var recovered = await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (!recovered.Connected) throw new InvalidOperationException("Host did not start a clean Worker after hard recovery.");
    }

    private static HostOptions CreateOptions(int requestTimeoutSeconds, int hardRecoveryTimeoutSeconds)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable.");
        var logDirectory = Path.Combine(Path.GetTempPath(), "ZemaxMCP-private-rpc-tests", Guid.NewGuid().ToString("N"));
        return HostOptions.Parse(new[]
        {
            "--worker", executable,
            "--host", "127.0.0.1",
            "--port", "8000",
            "--log-dir", logDirectory,
            "--request-timeout-seconds", requestTimeoutSeconds.ToString(),
            "--hard-recovery-timeout-seconds", hardRecoveryTimeoutSeconds.ToString()
        });
    }

    private static async Task VerifyMcpHttpToWorkerEndToEndAsync()
    {
        var root = FindRepositoryRoot();
        var host = Path.Combine(root, "src", "ZemaxMCP.HttpBridge", "bin", "Release", "net10.0-windows", "ZemaxMCP.Host.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("Build the Host before the MCP HTTP E2E test.", host);
        var worker = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable.");
        var port = ReserveLoopbackPort();
        var testRoot = Path.Combine(Path.GetTempPath(), "ZemaxMCP-mcp-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var workerLog = Path.Combine(testRoot, "fake-worker-started.txt");
        var startInfo = new ProcessStartInfo(host,
            $"--worker \"{worker}\" --host 127.0.0.1 --port {port} --log-dir \"{testRoot}\" --read-only true --allowed-host 127.0.0.1 --allowed-origin http://127.0.0.1:*")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["ZEMAX_MCP_TOKEN"] = "private-rpc-e2e-token";
        startInfo.Environment["ZEMAX_MCP_FAKE_WORKER_LOG"] = workerLog;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Host E2E process.");
        var succeeded = false;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            HttpResponseMessage? initialize = null;
            var lastInitializeFailure = string.Empty;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    initialize = await SendMcpAsync(client, endpoint,
                        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"private-rpc-e2e\",\"version\":\"1.0\"}}}", null).ConfigureAwait(false);
                    if (initialize.IsSuccessStatusCode) break;
                    lastInitializeFailure = ((int)initialize.StatusCode) + " " + await initialize.Content.ReadAsStringAsync().ConfigureAwait(false);
                    initialize.Dispose();
                }
                catch (HttpRequestException) { }
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (initialize == null || !initialize.IsSuccessStatusCode)
                throw new InvalidOperationException("The Host did not accept an MCP initialize request: " + lastInitializeFailure);
            var sessionId = initialize.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;
            initialize.Dispose();
            if (File.Exists(workerLog))
                throw new InvalidOperationException("Host startup or initialize unexpectedly started the Worker.");

            using var tools = await SendMcpAsync(client, endpoint,
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}", sessionId).ConfigureAwait(false);
            var toolsBody = await ReadFirstMcpPayloadAsync(tools).ConfigureAwait(false);
            if (!tools.IsSuccessStatusCode || !toolsBody.Contains("\"tools\"", StringComparison.Ordinal) || !File.Exists(workerLog))
                throw new InvalidOperationException("MCP HTTP tools/list did not reach the private Fake Worker.");

            using var spoofed = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health");
            spoofed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
            spoofed.Headers.Host = "attacker.example";
            // The Origin itself is configured as valid. Only Host filtering
            // may reject this request, proving Origin never trusts Host.
            spoofed.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:4567");
            using var rejected = await client.SendAsync(spoofed).ConfigureAwait(false);
            if (rejected.StatusCode != HttpStatusCode.BadRequest)
                throw new InvalidOperationException("Configured Host filtering did not reject a spoofed Host header.");
            succeeded = true;
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit(3000);
            if (succeeded) try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    private static async Task<HttpResponseMessage> SendMcpAsync(HttpClient client, Uri endpoint, string body, string? sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-11-25");
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
    }

    private static async Task<string> ReadFirstMcpPayloadAsync(HttpResponseMessage response)
    {
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
            while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                if (line.StartsWith("data:", StringComparison.Ordinal)) return line.Substring("data:".Length).Trim();
            throw new InvalidOperationException("MCP SSE response ended before a data payload was received.");
        }
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OpticStudioMCPServer.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static async Task RunFakeWorkerAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var secret = Environment.GetEnvironmentVariable("ZEMAX_MCP_PIPE_SECRET") ?? throw new InvalidOperationException("Missing pipe secret.");
        await writer.WriteLineAsync("ZEMAX_MCP_PIPE_HELLO|" + Environment.ProcessId + "|" + secret).ConfigureAwait(false);
        if (!string.Equals(await reader.ReadLineAsync().ConfigureAwait(false), "ZEMAX_MCP_PIPE_OK", StringComparison.Ordinal))
            throw new InvalidOperationException("Host rejected Fake Worker handshake.");

        var mode = Environment.GetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE") ?? string.Empty;
        var workerLog = Environment.GetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_LOG");
        if (!string.IsNullOrWhiteSpace(workerLog)) File.AppendAllText(workerLog, "started" + Environment.NewLine);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            var kind = root.GetProperty("kind").GetString();
            var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
            var operationId = root.GetProperty("operationId").GetString() ?? string.Empty;
            if (string.Equals(kind, ZemaxRpcProtocol.CancelOperation, StringComparison.Ordinal))
            {
                await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new { cancelled = true }).ConfigureAwait(false);
                continue;
            }
            if (string.Equals(kind, ZemaxRpcProtocol.GetStatus, StringComparison.Ordinal) && string.Equals(mode, "hang-status", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return;
            }
            if (string.Equals(kind, ZemaxRpcProtocol.GetStatus, StringComparison.Ordinal))
            {
                await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId,
                    new { zosApiLoaded = true, connected = true, connectionMode = "fake" }).ConfigureAwait(false);
                if (string.Equals(mode, "exit-after-status", StringComparison.Ordinal)) return;
                continue;
            }
            if (string.Equals(kind, ZemaxRpcProtocol.GetToolCatalog, StringComparison.Ordinal))
            {
                await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new { tools = Array.Empty<object>() }).ConfigureAwait(false);
                continue;
            }
            await SendAsync(writer, ZemaxRpcProtocol.Error, requestId, operationId,
                new { code = "unsupported_command", message = "Fake Worker does not support this command." }).ConfigureAwait(false);
        }
    }

    private static Task SendAsync(StreamWriter writer, string kind, string requestId, string operationId, object payload)
    {
        var message = new ZemaxRpcEnvelope
        {
            Kind = kind,
            RequestId = requestId,
            OperationId = operationId,
            Payload = JsonSerializer.SerializeToElement(payload)
        };
        return writer.WriteLineAsync(JsonSerializer.Serialize(message));
    }
}
