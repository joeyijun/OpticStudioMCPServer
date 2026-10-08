using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ZemaxMCP.HttpBridge.ModernHost;
using ZemaxMCP.Rpc;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.PrivateRpcTests;

internal static class Program
{
    private static readonly JsonSerializerOptions PrivateRpcJson = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--pipe", StringComparison.OrdinalIgnoreCase))
        {
            await RunFakeWorkerAsync(args[1]).ConfigureAwait(false);
            return 0;
        }

        try
        {
            VerifyActivityOwnership();
            VerifyOriginBoundary();
            await VerifyBackgroundJobLeaseRetentionAsync().ConfigureAwait(false);
            await VerifyCancelledLeaseWaitAsync().ConfigureAwait(false);
            ScopedCredentialAssertions.Verify();
            VerifyJobOwnershipRegistry();
            VerifyStrictArgumentBinding();
            await VerifyWriteGateCancellationClassificationAsync().ConfigureAwait(false);
            await VerifyContractMismatchRejectedAsync().ConfigureAwait(false);
            await VerifyPipeFaultRecoveryAsync().ConfigureAwait(false);
            await VerifyHardTimeoutRecoveryAsync().ConfigureAwait(false);
            await VerifyClientCancellationRecoveryBarrierAsync().ConfigureAwait(false);
            await VerifyProgressEventDispatchAsync().ConfigureAwait(false);
            await VerifyMcpHttpToWorkerEndToEndAsync().ConfigureAwait(false);
            await VerifyScopedCredentialHttpAsync().ConfigureAwait(false);
            Console.WriteLine("Private RPC v3 contract negotiation, recovery, event dispatch, static discovery, identity, Origin, and MCP HTTP E2E verification passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void VerifyJobOwnershipRegistry()
    {
        var registry = new JobOwnerRegistry();
        registry.Register("scoped:a", "owned-1", 7);
        registry.Register("scoped:b", "owned-2", 7);
        if (!registry.IsOwned("scoped:a", "owned-1", 7) ||
            registry.IsOwned("scoped:b", "owned-1", 7) ||
            registry.IsOwned("scoped:a", "owned-1", 8))
            throw new InvalidOperationException("Job ownership is not bound to its authenticated client and Worker generation.");

        var mixed = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = "[{\"jobId\":\"owned-1\",\"result\":\"client-a-private\"}," +
                           "{\"jobId\":\"owned-2\",\"result\":\"client-b-private\"}," +
                           "{\"jobId\":\"unknown\",\"result\":\"other-private\"}]"
                }
            },
            IsError = false
        };
        var filtered = JobOwnerRegistry.FilterList(mixed, "scoped:a", 7, registry);
        var text = (filtered.Content.Single() as TextContentBlock)?.Text ?? string.Empty;
        if (filtered.IsError == true || !text.Contains("client-a-private", StringComparison.Ordinal) ||
            text.Contains("client-b-private", StringComparison.Ordinal) ||
            text.Contains("other-private", StringComparison.Ordinal))
            throw new InvalidOperationException("Job list did not remove foreign result payloads.");

        var invalid = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"unexpected\":true}" } },
            IsError = false
        };
        if (JobOwnerRegistry.FilterList(invalid, "scoped:a", 7, registry).IsError != true)
            throw new InvalidOperationException("Malformed Worker job-list data did not fail closed.");

        registry.ReleaseGeneration(7);
        if (registry.IsOwned("scoped:a", "owned-1", 7))
            throw new InvalidOperationException("Worker generation change did not revoke old Job IDs.");

        for (var index = 0; index < JobOwnerRegistry.MaximumRecords + 10; index++)
            registry.Register("scoped:a", "bounded-" + index, 8);
        if (registry.IsOwned("scoped:a", "bounded-0", 8) ||
            !registry.IsOwned("scoped:a", "bounded-" + (JobOwnerRegistry.MaximumRecords + 9), 8))
            throw new InvalidOperationException("Bounded job owner history did not drop stale entries safely.");
    }

    private static void VerifyActivityOwnership()
    {
        var monitor = new McpActivityMonitor();
        using var first = monitor.Begin("client:codex@1.0|remote:192.168.8.20", "zemax_get_system");
        using (monitor.Begin("client:claude@1.0|remote:192.168.8.21", "zemax_status"))
        {
            var active = monitor.GetHealth();
            if (active.ActiveRequests != 2 || active.ActiveOperations.Count != 2 ||
                active.ActiveOperations[0].Client != "client:codex@1.0|remote:192.168.8.20" ||
                active.ActiveOperations[0].Tool != "zemax_get_system" ||
                active.ActiveOperations[1].Client != "client:claude@1.0|remote:192.168.8.21")
                throw new InvalidOperationException("Concurrent MCP activity lost its client/tool ownership.");
        }
        var remaining = monitor.GetHealth();
        if (remaining.ActiveRequests != 1 || remaining.ActiveOperations[0].Tool != "zemax_get_system")
            throw new InvalidOperationException("Completed MCP activity was not removed independently.");
        first.Dispose();
        var completed = monitor.GetHealth();
        if (completed.ActiveRequests != 0 || completed.LastClient != "client:codex@1.0|remote:192.168.8.20" ||
            completed.LastTool != "zemax_get_system" || completed.LastRequestAt == null)
            throw new InvalidOperationException("The latest completed call was attributed to the wrong AI client.");
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

    private static async Task VerifyBackgroundJobLeaseRetentionAsync()
    {
        var lease = new OpticStudioControlLease(TimeSpan.FromMilliseconds(50));
        using (await lease.AcquireAsync("client-a", "zemax_global_search", CancellationToken.None).ConfigureAwait(false)) { }
        if (!lease.RetainForJob("client-a", "job-1", generation: 7))
            throw new InvalidOperationException("The owning client could not retain control for its background job.");

        await Task.Delay(80).ConfigureAwait(false);
        try
        {
            using var _ = await lease.AcquireAsync("client-b", "zemax_status", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A background job did not prevent lease expiry and cross-client takeover.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("currently leased", StringComparison.OrdinalIgnoreCase)) { }

        lease.ObserveJob(8, new WorkerJobStatus { JobId = "job-1", State = "Completed" });
        await Task.Delay(80).ConfigureAwait(false);
        try
        {
            using var _ = await lease.AcquireAsync("client-b", "zemax_status", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A terminal event from the wrong Worker generation released the job lease.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("currently leased", StringComparison.OrdinalIgnoreCase)) { }

        lease.ReleaseGeneration(7);
        await Task.Delay(80).ConfigureAwait(false);
        using var handedOff = await lease.AcquireAsync("client-b", "zemax_status", CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task VerifyCancelledLeaseWaitAsync()
    {
        var lease = new OpticStudioControlLease(TimeSpan.FromMinutes(15));
        using var active = await lease.AcquireAsync("client-a", "active-operation", CancellationToken.None).ConfigureAwait(false);

        var initialHealth = lease.GetHealth();
        var lastActivityProperty = initialHealth.GetType().GetProperty("lastActivity")
            ?? throw new InvalidOperationException("Control lease health no longer exposes lastActivity.");
        var initialActivity = (DateTimeOffset?)lastActivityProperty.GetValue(initialHealth);

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        try
        {
            using var unexpected = await lease.AcquireAsync("client-a", "cancelled-waiter", cancelled.Token).ConfigureAwait(false);
            throw new InvalidOperationException("A blocked control-lease waiter ignored cancellation.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }

        var afterHealth = lease.GetHealth();
        var afterActivity = (DateTimeOffset?)lastActivityProperty.GetValue(afterHealth);
        if (initialActivity != afterActivity)
            throw new InvalidOperationException("A cancelled waiter changed control-lease activity before acquiring the execution gate.");
        var operation = afterHealth.GetType().GetProperty("activeOperation")?.GetValue(afterHealth) as string;
        if (operation != "active-operation")
            throw new InvalidOperationException("A cancelled waiter replaced the currently running control operation.");

        active.Dispose();
        using var followupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var followup = await lease.AcquireAsync("client-a", "followup-operation", followupTimeout.Token).ConfigureAwait(false);
    }

    private static void VerifyStrictArgumentBinding()
    {
        var method = typeof(Program).GetMethod(nameof(StrictBinderFixture), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not resolve the strict-binder test fixture.");

        using (var valid = JsonDocument.Parse("{\"required\":\"ok\",\"optional\":3}"))
        {
            var token = new CancellationTokenSource().Token;
            var values = WorkerToolRegistry.BindArguments(method, valid.RootElement, token);
            if (!string.Equals(values[0] as string, "ok", StringComparison.Ordinal) ||
                values[1] is not int optional || optional != 3 ||
                values[2] is not CancellationToken boundToken || boundToken != token)
                throw new InvalidOperationException("Worker argument binding did not preserve valid typed arguments and cancellation.");
        }

        using (var typo = JsonDocument.Parse("{\"required\":\"ok\",\"optoinal\":3}"))
        {
            try
            {
                WorkerToolRegistry.BindArguments(method, typo.RootElement, CancellationToken.None);
                throw new InvalidOperationException("An unknown Worker tool argument was silently ignored.");
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Unknown tool argument", StringComparison.Ordinal) &&
                                               ex.Message.Contains("optoinal", StringComparison.Ordinal)) { }
        }

        using (var missing = JsonDocument.Parse("{\"optional\":3}"))
        {
            try
            {
                WorkerToolRegistry.BindArguments(method, missing.RootElement, CancellationToken.None);
                throw new InvalidOperationException("A missing required Worker tool argument was accepted.");
            }
            catch (ArgumentException ex) when (ex.Message.Contains("Missing required argument: required", StringComparison.Ordinal)) { }
        }
    }

    private static void StrictBinderFixture(string required, int optional = 7, CancellationToken cancellationToken = default) { }

    private static async Task VerifyWriteGateCancellationClassificationAsync()
    {
        using var blockedGate = new SemaphoreSlim(0, 1);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var callerDeadline = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        callerDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await WorkerRpcClient.WaitForWriteGateAsync(blockedGate, callerDeadline.Token, caller.Token, callerDeadline).ConfigureAwait(false);
            throw new InvalidOperationException("A caller-cancelled write-lock wait unexpectedly acquired the gate.");
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Caller cancellation while waiting for the RPC write lock was misclassified as a write timeout.");
        }

        using var timeoutGate = new SemaphoreSlim(0, 1);
        using var writeDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        try
        {
            await WorkerRpcClient.WaitForWriteGateAsync(timeoutGate, writeDeadline.Token, CancellationToken.None, writeDeadline).ConfigureAwait(false);
            throw new InvalidOperationException("An expired write deadline unexpectedly acquired the gate.");
        }
        catch (TimeoutException) { }
    }

    private static async Task VerifyContractMismatchRejectedAsync()
    {
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", "bad-manifest");
        await using var client = new WorkerRpcClient(CreateOptions(10, 20));
        try
        {
            await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A Worker with a mismatched tool manifest was accepted.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("manifest", StringComparison.OrdinalIgnoreCase)) { }
        finally { Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", null); }
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
        await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var hung = client.CallToolAsync(TestTool("zemax_test_hang"), cancelled.Token);
        await Task.Delay(75).ConfigureAwait(false);
        var queued = client.CallToolAsync(TestTool("zemax_test_echo"), CancellationToken.None);
        try
        {
            await hung.ConfigureAwait(false);
            throw new InvalidOperationException("The intentionally cancelled Worker request unexpectedly completed.");
        }
        catch (OperationCanceledException) { }

        await Task.Delay(250).ConfigureAwait(false);
        if (queued.IsCompleted)
            throw new InvalidOperationException("A request queued before cancellation bypassed the cancelled-operation recovery barrier.");
        var recovered = await queued.ConfigureAwait(false);
        if (recovered.IsError == true || recovered.Content.Count == 0)
            throw new InvalidOperationException("Worker did not recover after client cancellation drained its generation.");
    }

    private static async Task VerifyProgressEventDispatchAsync()
    {
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", null);
        await using var client = new WorkerRpcClient(CreateOptions(10, 20));
        await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var observed = new TaskCompletionSource<OperationProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = client.CallToolAsync(TestTool("zemax_get_system"), CancellationToken.None,
            (progress, _) =>
            {
                observed.TrySetResult(progress);
                return Task.CompletedTask;
            });
        var progressEvent = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        if (progressEvent.Fraction != 0.5 || progressEvent.ToolName != "zemax_get_system")
            throw new InvalidOperationException("Worker progress was not dispatched to the matching Host operation.");
        var result = await call.ConfigureAwait(false);
        if (result.IsError == true) throw new InvalidOperationException("Progress dispatch interfered with the final Worker result.");
        var health = JsonSerializer.Serialize(client.GetHealth());
        if (!health.Contains("eventJobs", StringComparison.Ordinal) || !health.Contains("zemax_get_system", StringComparison.Ordinal))
            throw new InvalidOperationException("Worker event state was not retained for diagnostics.");
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
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            await Task.Delay(250).ConfigureAwait(false);
            if (File.Exists(workerLog)) throw new InvalidOperationException("Host startup unexpectedly started the Worker.");

            HttpResponseMessage? list = null;
            var lastModernFailure = string.Empty;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    list = await Send2026ListToolsAsync(client, endpoint, 1, "client-a", "instance-a").ConfigureAwait(false);
                    if (list.IsSuccessStatusCode) break;
                    lastModernFailure = ((int)list.StatusCode) + " " + await list.Content.ReadAsStringAsync().ConfigureAwait(false);
                    list.Dispose();
                }
                catch (HttpRequestException ex) { lastModernFailure = ex.Message; }
                catch (TaskCanceledException ex) { lastModernFailure = ex.Message; }
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (list == null || !list.IsSuccessStatusCode)
                throw new InvalidOperationException("The Host did not accept a 2026-07-28 stateless tools/list: " + lastModernFailure);
            using (list)
            {
                var listBody = await ReadFirstMcpPayloadAsync(list).ConfigureAwait(false);
                if (!listBody.Contains("zemax_status", StringComparison.Ordinal) || !listBody.Contains("zemax_open_file", StringComparison.Ordinal))
                    throw new InvalidOperationException("Read-only Host tools/list did not preserve ReadOnly and Caution tools.");
                if (listBody.Contains("zemax_set_surface", StringComparison.Ordinal))
                    throw new InvalidOperationException("Read-only Host tools/list exposed a HighImpact tool that execution policy would reject.");
                if (File.Exists(workerLog))
                    throw new InvalidOperationException("tools/list started the Worker; static Host discovery is not independent of ZOS-API.");
            }

            using var blocked = await Send2026ToolCallAsync(client, endpoint, 2, "zemax_set_surface", "client-a", "instance-a").ConfigureAwait(false);
            var blockedBody = await ReadFirstMcpPayloadAsync(blocked).ConfigureAwait(false);
            if (!blockedBody.Contains("does not permit", StringComparison.OrdinalIgnoreCase) ||
                !blockedBody.Contains("isError", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A direct tools/call bypassed the read-only static manifest policy: " + blockedBody);
            if (File.Exists(workerLog))
                throw new InvalidOperationException("A policy-rejected tools/call started the Worker before Host authorization completed.");

            using var echo = await Send2026ToolCallAsync(client, endpoint, 3, "zemax_status", "client-a", "instance-a").ConfigureAwait(false);
            var echoBody = await ReadFirstMcpPayloadAsync(echo).ConfigureAwait(false);
            if (!echo.IsSuccessStatusCode || !echoBody.Contains("echo-ok", StringComparison.Ordinal) || !File.Exists(workerLog))
                throw new InvalidOperationException("2026 MCP tools/call did not lazy-start and traverse Host, control lease, and Fake Worker.");

            using var healthRequest = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health");
            healthRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
            using var health = await client.SendAsync(healthRequest).ConfigureAwait(false);
            var healthBody = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!health.IsSuccessStatusCode || !healthBody.Contains("\"licenseStatus\":\"fake-license\"", StringComparison.Ordinal) ||
                !healthBody.Contains(StaticToolManifest.ContractFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Structured health did not preserve license and authenticated contract identity.");

            using var heldResponse = await Send2026ToolCallAsync(client, endpoint, 4, "zemax_get_system", "client-a", "instance-a").ConfigureAwait(false);
            if (!heldResponse.IsSuccessStatusCode) throw new InvalidOperationException("The first client could not retain the control lease across tool names.");
            await Task.Delay(150).ConfigureAwait(false);
            using (var activeHealthRequest = new HttpRequestMessage(HttpMethod.Get, endpoint + "/activity"))
            {
                activeHealthRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
                using var activeHealthResponse = await client.SendAsync(activeHealthRequest).ConfigureAwait(false);
                using var activeHealth = JsonDocument.Parse(await activeHealthResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                var activeOperations = activeHealth.RootElement.GetProperty("activeOperations");
                if (!activeHealthResponse.IsSuccessStatusCode || activeOperations.GetArrayLength() == 0 ||
                    !activeOperations[0].GetProperty("client").GetString()!.Contains("client-a", StringComparison.Ordinal) ||
                    activeOperations[0].GetProperty("tool").GetString() != "zemax_get_system")
                    throw new InvalidOperationException("Remote activity did not identify the AI client and tool during an active call.");
            }
            // Same clientInfo and same IP, but a different explicit instance ID.
            using var rejectedLease = await Send2026ToolCallAsync(client, endpoint, 5, "zemax_status", "client-a", "instance-b").ConfigureAwait(false);
            var rejectedLeaseBody = await ReadFirstMcpPayloadAsync(rejectedLease).ConfigureAwait(false);
            if (!rejectedLeaseBody.Contains("currently leased", StringComparison.OrdinalIgnoreCase) &&
                !rejectedLeaseBody.Contains("isError", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Two same-name same-IP MCP client instances collapsed into one control identity: " + rejectedLeaseBody);

            var heldBody = await ReadFirstMcpPayloadAsync(heldResponse).ConfigureAwait(false);
            if (!heldBody.Contains("echo-ok", StringComparison.Ordinal))
                throw new InvalidOperationException("The held control-lease request did not complete normally.");

            using var disconnect = await Send2026ToolCallAsync(client, endpoint, 6, "zemax_disconnect", "client-a", "instance-a").ConfigureAwait(false);
            var disconnectBody = await ReadFirstMcpPayloadAsync(disconnect).ConfigureAwait(false);
            if (!disconnect.IsSuccessStatusCode || !disconnectBody.Contains("success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The lease owner could not disconnect cleanly: " + disconnectBody);

            using var handedOff = await Send2026ToolCallAsync(client, endpoint, 7, "zemax_status", "client-a", "instance-b").ConfigureAwait(false);
            var handedOffBody = await ReadFirstMcpPayloadAsync(handedOff).ConfigureAwait(false);
            if (!handedOff.IsSuccessStatusCode || !handedOffBody.Contains("echo-ok", StringComparison.Ordinal))
                throw new InvalidOperationException("A successful zemax_disconnect did not release control ownership for immediate handoff: " + handedOffBody);

            using var spoofed = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health");
            spoofed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
            spoofed.Headers.Host = "attacker.example";
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

    private static async Task VerifyScopedCredentialHttpAsync()
    {
        var root = FindRepositoryRoot();
        var host = Path.Combine(root, "src", "ZemaxMCP.HttpBridge", "bin", "Release", "net10.0-windows", "ZemaxMCP.Host.exe");
        var worker = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable.");
        var port = ReserveLoopbackPort();
        var testRoot = Path.Combine(Path.GetTempPath(), "ZemaxMCP-scoped-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var file = Path.Combine(testRoot, "clients.json");
        var workerLog = Path.Combine(testRoot, "fake-worker-started.txt");
        const string reader = "reader-credential-e2e-test-01234567890123456789";
        const string writer = "writer-credential-e2e-test-01234567890123456789";
        const string otherWriter = "other-writer-e2e-98765432109876543210987654321";
        ScopedCredentialAssertions.Write(file,
            ("reader", "read-only", reader), ("writer", "read-write", writer),
            ("writer-two", "read-write", otherWriter));
        var startInfo = new ProcessStartInfo(host,
            $"--worker \"{worker}\" --host 127.0.0.1 --port {port} --log-dir \"{testRoot}\" " +
            $"--client-credentials-file \"{file}\" --allowed-host 127.0.0.1 --allowed-origin http://127.0.0.1:*")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Remove("ZEMAX_MCP_TOKEN");
        startInfo.Environment.Remove("ZEMAX_MCP_CLIENTS_FILE");
        startInfo.Environment["ZEMAX_MCP_FAKE_WORKER_LOG"] = workerLog;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start scoped Host E2E.");
        var success = false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            HttpResponseMessage? first = null;
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    first = await SendScopedAsync(client, endpoint, 101, "tools/list", null, reader).ConfigureAwait(false);
                    if (first.IsSuccessStatusCode) break;
                    first.Dispose();
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (first == null || !first.IsSuccessStatusCode)
                throw new InvalidOperationException("Scoped Host did not accept an authenticated stateless tools/list.");
            using (first)
            {
                var response = await ReadFirstMcpPayloadAsync(first).ConfigureAwait(false);
                if (!response.Contains("zemax_get_system", StringComparison.Ordinal) ||
                    response.Contains("zemax_open_file", StringComparison.Ordinal) ||
                    response.Contains("zemax_set_surface", StringComparison.Ordinal) ||
                    response.Contains("zemax_job_cancel", StringComparison.Ordinal))
                    throw new InvalidOperationException("Read-only scoped credential leaked a Caution or HighImpact command.");
            }

            using (var denied = await SendScopedAsync(client, endpoint, 102, "tools/call", "zemax_open_file", reader).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(denied).ConfigureAwait(false);
                if (!body.Contains("does not permit", StringComparison.OrdinalIgnoreCase) ||
                    !body.Contains("isError", StringComparison.OrdinalIgnoreCase) || File.Exists(workerLog))
                    throw new InvalidOperationException("Scoped read-only call bypassed authorization before Worker startup.");
            }

            using (var list = await SendScopedAsync(client, endpoint, 103, "tools/list", null, writer).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(list).ConfigureAwait(false);
                if (!list.IsSuccessStatusCode || !body.Contains("zemax_set_surface", StringComparison.Ordinal) ||
                    !body.Contains("zemax_open_file", StringComparison.Ordinal) ||
                    !body.Contains("zemax_job_cancel", StringComparison.Ordinal) ||
                    body.Contains("zemax_multistart_status", StringComparison.Ordinal) ||
                    body.Contains("zemax_multistart_stop", StringComparison.Ordinal))
                    throw new InvalidOperationException("Write credential did not enforce modern Job-only access and authorized tools.");
            }

            using (var run = await SendScopedAsync(client, endpoint, 104, "tools/call", "zemax_status", writer).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(run).ConfigureAwait(false);
                if (!run.IsSuccessStatusCode || !body.Contains("echo-ok", StringComparison.Ordinal) ||
                    !File.Exists(workerLog))
                    throw new InvalidOperationException("Scoped writer did not reach the Worker.");
            }

            // Both clients deliberately advertise the same clientInfo and
            // instance ID. The authenticated token ID must own the lease.
            using (var competing = await SendScopedAsync(client, endpoint, 105, "tools/call", "zemax_status", otherWriter).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(competing).ConfigureAwait(false);
                if (body.Contains("echo-ok", StringComparison.Ordinal) ||
                    (!body.Contains("currently leased", StringComparison.OrdinalIgnoreCase) &&
                     !body.Contains("isError", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Two distinct authenticated tokens shared one control lease: " + body);
            }

            using (var release = await SendScopedAsync(client, endpoint, 106, "tools/call", "zemax_disconnect", writer).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(release).ConfigureAwait(false);
                if (!release.IsSuccessStatusCode || !body.Contains("success", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Scoped owner could not release its lease.");
            }
            using (var handoff = await SendScopedAsync(client, endpoint, 107, "tools/call", "zemax_status", otherWriter).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(handoff).ConfigureAwait(false);
                if (!handoff.IsSuccessStatusCode || !body.Contains("echo-ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("Authenticated second client could not acquire the lease after disconnect.");
            }

            // Only the job creator may view its status/results, list it,
            // or cancel it; a different authenticated writer is denied.
            using (var started = await SendScopedAsync(client, endpoint, 108, "tools/call", "zemax_run_nsc_ray_trace", otherWriter).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(started).ConfigureAwait(false);
                if (!started.IsSuccessStatusCode || !body.Contains("fake-owned-job", StringComparison.Ordinal))
                    throw new InvalidOperationException("The scoped test Job did not register its creator: " + body);
            }
            using (var foreignStatus = await SendScopedAsync(client, endpoint, 109, "tools/call", "zemax_job_status", writer,
                       new { jobId = "fake-owned-job" }).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(foreignStatus).ConfigureAwait(false);
                if (!body.Contains("isError", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("private-job-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Foreign writer was able to read a scoped Job: " + body);
            }
            using (var ownerStatus = await SendScopedAsync(client, endpoint, 110, "tools/call", "zemax_job_status", otherWriter,
                       new { jobId = "fake-owned-job" }).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(ownerStatus).ConfigureAwait(false);
                if (!ownerStatus.IsSuccessStatusCode || !body.Contains("private-job-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Job creator could not read its own status/result: " + body);
            }
            using (var foreignList = await SendScopedAsync(client, endpoint, 111, "tools/call", "zemax_job_list", writer).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(foreignList).ConfigureAwait(false);
                if (!foreignList.IsSuccessStatusCode ||
                    body.Contains("fake-owned-job", StringComparison.Ordinal) ||
                    body.Contains("private-job-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Foreign writer saw another client's Job history: " + body);
            }
            using (var ownerList = await SendScopedAsync(client, endpoint, 112, "tools/call", "zemax_job_list", otherWriter).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(ownerList).ConfigureAwait(false);
                if (!ownerList.IsSuccessStatusCode ||
                    !body.Contains("fake-owned-job", StringComparison.Ordinal) ||
                    !body.Contains("private-job-result", StringComparison.Ordinal) ||
                    body.Contains("private-unknown-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped Job listing did not filter unowned results: " + body);
            }
            using (var foreignCancel = await SendScopedAsync(client, endpoint, 113, "tools/call", "zemax_job_cancel", writer,
                       new { jobId = "fake-owned-job" }).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(foreignCancel).ConfigureAwait(false);
                if (!body.Contains("isError", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Foreign writer could cancel another client's Job: " + body);
            }
            using (var scopedHealth = await SendScopedGetAsync(client, endpoint + "/health", otherWriter).ConfigureAwait(false))
            {
                var body = await scopedHealth.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!scopedHealth.IsSuccessStatusCode || !body.Contains("scoped", StringComparison.Ordinal) ||
                    body.Contains("fake-owned-job", StringComparison.Ordinal) ||
                    body.Contains("eventJobs", StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped /health leaked cross-client Jobs or Worker event state.");
            }
            using (var scopedActivity = await SendScopedGetAsync(client, endpoint + "/activity", writer).ConfigureAwait(false))
            {
                var body = await scopedActivity.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!scopedActivity.IsSuccessStatusCode || body.Contains("fake-owned-job", StringComparison.Ordinal) ||
                    body.Contains("activeOperations", StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped /activity leaked cross-client operations.");
            }
            using (var ownerCancel = await SendScopedAsync(client, endpoint, 114, "tools/call", "zemax_job_cancel", otherWriter,
                       new { jobId = "fake-owned-job" }).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(ownerCancel).ConfigureAwait(false);
                if (!ownerCancel.IsSuccessStatusCode || !body.Contains("Cancelled", StringComparison.Ordinal))
                    throw new InvalidOperationException("Job creator could not cancel its own Job: " + body);
            }

            // Removing the reader revokes its bearer immediately. The Host
            // must not cache stale auth decisions or silently fall back.
            ScopedCredentialAssertions.Write(file, ("writer", "read-write", writer), ("writer-two", "read-write", otherWriter));
            using (var revoked = await SendScopedAsync(client, endpoint, 105, "tools/list", null, reader).ConfigureAwait(false))
                if (revoked.StatusCode != HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Revoked read-only credential was still accepted.");

            File.WriteAllText(file, "{ malformed");
            using (var malformed = await SendScopedAsync(client, endpoint, 106, "tools/list", null, writer).ConfigureAwait(false))
                if (malformed.StatusCode != HttpStatusCode.ServiceUnavailable)
                    throw new InvalidOperationException("Malformed live credential update did not fail closed.");

            success = true;
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit(3000);
            if (success) try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    private static Task<HttpResponseMessage> SendScopedAsync(
        HttpClient client, Uri endpoint, int id, string method, string? toolName, string bearer, object? arguments = null)
    {
        var body = Build2026Body(id, method, toolName, "spoofable-client-name", "same-instance-id");
        if (arguments != null)
            body = body.Replace("\"arguments\":{}", "\"arguments\":" + JsonSerializer.Serialize(arguments), StringComparison.Ordinal);
        var request = Create2026Request(endpoint, body, method, toolName);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static Task<HttpResponseMessage> SendScopedGetAsync(HttpClient client, Uri endpoint, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Send2026ListToolsAsync(HttpClient client, Uri endpoint, int id, string clientName, string instanceId)
    {
        var body = Build2026Body(id, "tools/list", null, clientName, instanceId);
        return client.SendAsync(Create2026Request(endpoint, body, "tools/list", null), HttpCompletionOption.ResponseHeadersRead);
    }

    private static Task<HttpResponseMessage> Send2026ToolCallAsync(HttpClient client, Uri endpoint, int id, string toolName, string clientName, string instanceId)
    {
        var body = Build2026Body(id, "tools/call", toolName, clientName, instanceId);
        return client.SendAsync(Create2026Request(endpoint, body, "tools/call", toolName), HttpCompletionOption.ResponseHeadersRead);
    }

    private static string Build2026Body(int id, string method, string? toolName, string clientName, string instanceId)
    {
        var parameters = toolName == null ? "" : "\"name\":\"" + toolName + "\",\"arguments\":{},";
        return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":{" + parameters +
            "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"" + clientName +
            "\",\"version\":\"1.0\"},\"io.modelcontextprotocol/clientCapabilities\":{},\"io.zemaxmcp/clientInstanceId\":\"" + instanceId + "\"}}}";
    }

    private static HttpRequestMessage Create2026Request(Uri endpoint, string body, string method, string? toolName)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", method);
        if (toolName != null) request.Headers.TryAddWithoutValidation("Mcp-Name", toolName);
        return request;
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
        var mode = Environment.GetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE") ?? string.Empty;
        var fingerprint = string.Equals(mode, "bad-manifest", StringComparison.Ordinal)
            ? new string('0', StaticToolManifest.ContractFingerprint.Length)
            : StaticToolManifest.ContractFingerprint;
        await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerHandshake
        {
            RpcVersion = ZemaxRpcProtocol.Version,
            WorkerProcessId = Environment.ProcessId,
            Secret = secret,
            ManifestFingerprint = fingerprint
        }, PrivateRpcJson)).ConfigureAwait(false);
        var acknowledgementLine = await reader.ReadLineAsync().ConfigureAwait(false);
        var acknowledgement = string.IsNullOrWhiteSpace(acknowledgementLine)
            ? null
            : JsonSerializer.Deserialize<WorkerHandshakeAck>(acknowledgementLine, PrivateRpcJson);
        if (acknowledgement == null || !acknowledgement.Accepted)
        {
            if (string.Equals(mode, "bad-manifest", StringComparison.Ordinal)) return;
            throw new InvalidOperationException("Host rejected Fake Worker handshake: " + acknowledgement?.Error);
        }

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
                        rpcVersion = ZemaxRpcProtocol.Version,
                        manifestFingerprint = StaticToolManifest.ContractFingerprint,
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
            if (string.Equals(kind, ZemaxRpcProtocol.InvokeTool, StringComparison.Ordinal))
            {
                var command = root.GetProperty("payload").GetProperty("command").GetString();
                if (string.Equals(command, "zemax_test_hang", StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                    return;
                }
                if (string.Equals(command, "zemax_get_system", StringComparison.Ordinal) || string.Equals(command, "zemax_test_hold", StringComparison.Ordinal))
                {
                    await SendAsync(writer, ZemaxRpcProtocol.Progress, string.Empty, operationId, new
                    {
                        operationId,
                        toolName = command,
                        fraction = 0.5,
                        queuePosition = 0,
                        state = "running",
                        message = "Fake progress"
                    }).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                if (string.Equals(command, "zemax_run_nsc_ray_trace", StringComparison.Ordinal))
                {
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text", text = "{\"success\":true,\"jobId\":\"fake-owned-job\",\"state\":\"Queued\"}" } },
                        isError = false
                    }).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(command, "zemax_job_status", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_job_cancel", StringComparison.Ordinal))
                {
                    var cancelling = string.Equals(command, "zemax_job_cancel", StringComparison.Ordinal);
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text", text =
                            "{\"jobId\":\"fake-owned-job\",\"state\":\"" + (cancelling ? "Cancelled" : "Running") +
                            "\",\"result\":\"private-job-result\"}" } },
                        isError = false
                    }).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(command, "zemax_job_list", StringComparison.Ordinal))
                {
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text", text =
                            "[{\"jobId\":\"fake-owned-job\",\"result\":\"private-job-result\"}," +
                            "{\"jobId\":\"unowned-fake-job\",\"result\":\"private-unknown-result\"}]" } },
                        isError = false
                    }).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(command, "zemax_disconnect", StringComparison.Ordinal))
                {
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text", text = "{\"success\":true,\"error\":null}" } },
                        isError = false
                    }).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(command, "zemax_status", StringComparison.Ordinal) || string.Equals(command, "zemax_get_system", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_test_echo", StringComparison.Ordinal) || string.Equals(command, "zemax_test_hold", StringComparison.Ordinal))
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
            Payload = JsonSerializer.SerializeToElement(payload, PrivateRpcJson)
        };
        return writer.WriteLineAsync(JsonSerializer.Serialize(message, PrivateRpcJson));
    }
}
