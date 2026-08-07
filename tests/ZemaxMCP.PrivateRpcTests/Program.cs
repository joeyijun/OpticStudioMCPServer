using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
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
            await VerifyClientCancellationRecoveryBarrierAsync().ConfigureAwait(false);
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

    private static async Task VerifyClientCancellationRecoveryBarrierAsync()
    {
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", null);
        await using var client = new WorkerRpcClient(CreateOptions(10, 20));
        // Start the generation before applying cancellation. This makes the
        // test prove an in-flight Worker operation is drained, not merely a
        // startup that was cancelled before its request was written.
        await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await client.CallToolAsync(TestTool("zemax_test_hang"), cancelled.Token).ConfigureAwait(false);
            throw new InvalidOperationException("The intentionally cancelled Worker request unexpectedly completed.");
        }
        catch (OperationCanceledException) { }

        var nextRequest = client.CallToolAsync(TestTool("zemax_test_echo"), CancellationToken.None);
        await Task.Delay(250).ConfigureAwait(false);
        if (nextRequest.IsCompleted)
            throw new InvalidOperationException("A new request bypassed the cancelled-operation recovery barrier.");
        var recovered = await nextRequest.ConfigureAwait(false);
        if (recovered.IsError == true || recovered.Content.Count == 0)
            throw new InvalidOperationException("Worker did not recover after client cancellation drained its generation.");
    }

    private static CallToolRequestParams TestTool(string name) => new()
    {
        Name = name,
        Arguments = new Dictionary<string, JsonElement>()
    };

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
            // Worker startup on a cold CI runner can legitimately exceed two
            // seconds. Keep individual HTTP requests bounded, but leave enough
            // room for the first lazy Worker startup and the deliberate 3 s
            // fake hold operation.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            await Task.Delay(250).ConfigureAwait(false);
            if (File.Exists(workerLog))
                throw new InvalidOperationException("Host startup unexpectedly started the Worker.");

            HttpResponseMessage? echo = null;
            var lastModernFailure = string.Empty;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    echo = await Send2026ToolCallAsync(client, endpoint, 1, "zemax_test_echo", "client-a").ConfigureAwait(false);
                    if (echo.IsSuccessStatusCode) break;
                    lastModernFailure = ((int)echo.StatusCode) + " " + await echo.Content.ReadAsStringAsync().ConfigureAwait(false);
                    echo.Dispose();
                }
                catch (HttpRequestException ex) { lastModernFailure = ex.Message; }
                catch (TaskCanceledException ex) { lastModernFailure = ex.Message; }
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (echo == null || !echo.IsSuccessStatusCode)
                throw new InvalidOperationException("The Host did not accept a 2026-07-28 stateless tools/call: " + lastModernFailure);
            using (echo)
            {
                var echoBody = await ReadFirstMcpPayloadAsync(echo).ConfigureAwait(false);
                if (!echoBody.Contains("echo-ok", StringComparison.Ordinal) || !File.Exists(workerLog))
                    throw new InvalidOperationException("2026 MCP tools/call did not traverse Host, control lease, and Fake Worker.");
            }

            using var healthRequest = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health");
            healthRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
            using var health = await client.SendAsync(healthRequest).ConfigureAwait(false);
            var healthBody = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!health.IsSuccessStatusCode || !healthBody.Contains("\"licenseStatus\":\"fake-license\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Structured Worker health did not preserve the Worker license result.");

            var hold = Send2026ToolCallAsync(client, endpoint, 2, "zemax_test_hold", "client-a");
            using var heldResponse = await hold.ConfigureAwait(false);
            if (!heldResponse.IsSuccessStatusCode) throw new InvalidOperationException("The first client could not acquire the control lease.");
            await Task.Delay(150).ConfigureAwait(false);
            using var rejectedLease = await Send2026ToolCallAsync(client, endpoint, 3, "zemax_test_echo", "client-b").ConfigureAwait(false);
            var rejectedLeaseBody = await ReadFirstMcpPayloadAsync(rejectedLease).ConfigureAwait(false);
            if (!rejectedLeaseBody.Contains("currently leased", StringComparison.OrdinalIgnoreCase) &&
                !rejectedLeaseBody.Contains("isError", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A second MCP client bypassed the active OpticStudio control lease: " + rejectedLeaseBody);

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

    private static async Task<HttpResponseMessage> Send2026ToolCallAsync(HttpClient client, Uri endpoint, int id, string toolName, string clientName)
    {
        var body = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName + "\",\"arguments\":{},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"" + clientName + "\",\"version\":\"1.0\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/call");
        request.Headers.TryAddWithoutValidation("Mcp-Name", toolName);
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
                    new
                    {
                        zosApiLoaded = true,
                        connected = true,
                        connectionMode = "fake",
                        zosApiAssembly = "C:\\Fake\\ZOSAPI.dll",
                        opticStudioDataDirectory = "C:\\Fake\\Data",
                        currentLicenseStatus = "fake-license",
                        lastLicenseStatus = "fake-license",
                        licenseValidForApi = true,
                        snapshotDirectory = "C:\\Fake\\Snapshots",
                        lastSnapshotPath = "C:\\Fake\\Snapshots\\last.zos",
                        jobs = Array.Empty<object>()
                    }).ConfigureAwait(false);
                if (string.Equals(mode, "exit-after-status", StringComparison.Ordinal)) return;
                continue;
            }
            if (string.Equals(kind, ZemaxRpcProtocol.GetToolCatalog, StringComparison.Ordinal))
            {
                await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                {
                    tools = new[]
                    {
                        new { name = "zemax_test_echo", description = "Test echo", inputSchema = new { type = "object" } },
                        new { name = "zemax_test_hold", description = "Test lease hold", inputSchema = new { type = "object" } }
                    }
                }).ConfigureAwait(false);
                continue;
            }
            if (string.Equals(kind, ZemaxRpcProtocol.InvokeTool, StringComparison.Ordinal))
            {
                var command = root.GetProperty("payload").GetProperty("command").GetString();
                if (string.Equals(command, "zemax_test_hang", StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                    return;
                }
                if (string.Equals(command, "zemax_test_hold", StringComparison.Ordinal))
                {
                    await SendAsync(writer, ZemaxRpcProtocol.Progress, string.Empty, operationId, new
                    {
                        operationId,
                        toolName = "zemax_test_hold",
                        fraction = 0.5,
                        queuePosition = 0,
                        state = "running",
                        message = "Fake progress"
                    }).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                if (string.Equals(command, "zemax_test_echo", StringComparison.Ordinal) || string.Equals(command, "zemax_test_hold", StringComparison.Ordinal))
                {
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text", text = "echo-ok" } },
                        isError = false
                    }).ConfigureAwait(false);
                    continue;
                }
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
