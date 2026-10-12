using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using ZemaxMCP.DesktopShared;
using ZemaxMCP.HttpBridge.ModernHost;

namespace ZemaxMCP.PrivateRpcTests;

internal static partial class Program
{
    private static async Task VerifyUpgradeReliabilityAsync()
    {
        try { await Task.Run(() => HostMaintenanceClient.Prepare(int.MaxValue)); throw new Exception("Accepted an unverifiable old Host."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Cannot verify safe shutdown")) { }
        using (var spoof = new NamedPipeServerStream("ZemaxMcpMaintenance-" + int.MaxValue, PipeDirection.InOut, 1,
                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var connection = spoof.WaitForConnectionAsync();
            try { await Task.Run(() => HostMaintenanceClient.Prepare(int.MaxValue)); throw new Exception("Accepted a spoofed maintenance pipe."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("does not belong")) { }
            await connection;
        }
        var lease = new OpticStudioControlLease();
        using (await lease.AcquireAsync("failed-start", "no-worker", CancellationToken.None)) { }
        using (await lease.AcquireAsync("next-client", "retry-start", CancellationToken.None)) { }
        lease.ObserveGenerationStarted(1);
        using (await lease.AcquireAsync("a", "foreground", CancellationToken.None)) { }
        lease.ReleaseGeneration(1);
        lease.ObserveGenerationStarted(2);
        using (await lease.AcquireAsync("b", "after-fault", CancellationToken.None))
        {
            lease.ReleaseGeneration(1); // stale event cannot clear generation 2
        }
        try { using var denied = await lease.AcquireAsync("a", "stale-event", CancellationToken.None); throw new Exception("Stale fault revoked current ownership."); }
        catch (ControlLeaseConflictException) { }
        var active = await lease.AcquireAsync("b", "fault-in-call", CancellationToken.None);
        lease.ReleaseGeneration(2);
        lease.ObserveGenerationStarted(3);
        active.Dispose();
        using (await lease.AcquireAsync("c", "after-unwind", CancellationToken.None)) { }

        var queuedLease = new OpticStudioControlLease();
        queuedLease.ObserveGenerationStarted(1);
        using (await queuedLease.AcquireAsync("queued-client", "wait-for-recovery", CancellationToken.None))
        {
            queuedLease.ReleaseGeneration(1);
            queuedLease.ObserveGenerationStarted(2);
            queuedLease.BindInvocation("queued-client", 2);
            Assert(queuedLease.RetainForJob("queued-client", "new-job", 2), "A request queued during recovery could not retain its NEW generation's Job.");
        }
        queuedLease.ReleaseGeneration(1);
        Assert(queuedLease.HasActiveBackgroundJobs, "Stale generation event cleared a re-bound background Job.");
        try { using var denied = await queuedLease.AcquireAsync("other", "unsafe-overlap", CancellationToken.None); throw new Exception("Queued recovery lost background ownership."); }
        catch (ControlLeaseConflictException) { }
        queuedLease.ReleaseGeneration(2);
        queuedLease.ObserveGenerationStarted(3);
        using (await queuedLease.AcquireAsync("other", "handoff", CancellationToken.None)) { }

        // A cancelled operation must reject preparation until drain/hard recovery ends.
        var options = CreateOptions(10, 20);
        await using (var worker = new WorkerRpcClient(options))
        {
            await worker.GetStatusAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource(150);
            var hung = worker.CallToolAsync(TestTool("zemax_test_hang"), cancellation.Token);
            try { await hung; } catch (OperationCanceledException) { }
            try { await worker.PrepareShutdownAsync(CancellationToken.None); throw new Exception("Prepared a draining cancelled operation."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("draining")) { }
        }

        await VerifyMaintenanceHttpAsync();
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", "connecting-status");
        try
        {
            await using var connecting = new WorkerRpcClient(CreateOptions(10, 20));
            await connecting.GetStatusAsync(CancellationToken.None);
            try { await connecting.PrepareShutdownAsync(CancellationToken.None); throw new Exception("Prepared an initializing ZOS-API connection."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("initialization")) { }
        }
        finally { Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", null); }
        Console.WriteLine("Upgrade maintenance: PID verification, fail-closed legacy detection, busy/draining denial, ticket ownership, atomic freeze/resume, actual net48 update and generation-only lease faults passed.");
    }

    private static async Task VerifyMaintenanceHttpAsync()
    {
        var root = FindRepositoryRoot();
        var executable = Path.Combine(root, "src", "ZemaxMCP.HttpBridge", "bin", "Release", "net10.0-windows", "ZemaxMCP.Host.exe");
        var directory = Path.Combine(Path.GetTempPath(), "ZemaxMCP-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var install = Path.Combine(directory, "install");
        var staging = Path.Combine(directory, "staging");
        var installedHostFolder = Path.Combine(install, "Host");
        Directory.CreateDirectory(installedHostFolder);
        Directory.CreateDirectory(staging);
        var hostSource = Path.GetDirectoryName(executable)!;
        foreach (var file in Directory.GetFiles(hostSource, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(installedHostFolder, Path.GetRelativePath(hostSource, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        File.WriteAllText(Path.Combine(install, "Start-Zemax-MCP.exe"), "old-test-launcher");
        File.WriteAllText(Path.Combine(staging, "Start-Zemax-MCP.exe"), "new-test-launcher");
        var updater = Path.Combine(root, "src", "ZemaxMCP.Updater", "bin", "Release", "net48", "ZemaxMCP.Updater.exe");
        async Task<int> RunUpdaterAsync()
        {
            using var updating = Process.Start(new ProcessStartInfo(updater,
                $"--staging \"{staging}\" --install \"{install}\" --parent-pid 2147483647 --restart false")
                { UseShellExecute = false, CreateNoWindow = true })!;
            await updating.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return updating.ExitCode;
        }
        var port = ReserveLoopbackPort();
        var info = new ProcessStartInfo(Path.Combine(installedHostFolder, Path.GetFileName(executable)), $"--worker \"{Environment.ProcessPath}\" --port {port} --log-dir \"{directory}\" --enable-official-tasks false")
        { UseShellExecute = false, CreateNoWindow = true };
        info.Environment["ZEMAX_MCP_TOKEN"] = "private-rpc-e2e-token";
        using var process = Process.Start(info)!;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            for (var i = 0; i < 100; i++)
            {
                try { using var response = await Send2026ListToolsAsync(http, endpoint, 901, "maintenance", "m1"); if (response.IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            var hold = Send2026ToolCallAsync(http, endpoint, 902, "zemax_get_system", "maintenance", "m1");
            for (var i = 0; i < 100; i++)
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, endpoint + "/activity");
                probe.Headers.Authorization = new("Bearer", "private-rpc-e2e-token");
                using var response = await http.SendAsync(probe);
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.GetProperty("activeRequests").GetInt32() > 0) break;
                await Task.Delay(20);
            }
            try { HostMaintenanceClient.Prepare(process.Id); throw new Exception("Maintenance accepted active optical call."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("postponed")) { }
            Assert(await RunUpdaterAsync() != 0, "Actual net48 updater accepted a busy Host.");
            Assert(!process.HasExited && File.ReadAllText(Path.Combine(install, "Start-Zemax-MCP.exe")) == "old-test-launcher",
                "Busy update killed the Host or touched installed binaries.");
            using var completed = await hold;
            Assert((await ReadFirstMcpPayloadAsync(completed)).Contains("echo-ok"), "Busy preparation interrupted the active call.");
            var workerPid = HostMaintenanceClient.Prepare(process.Id);
            Assert(workerPid > 0, "Maintenance did not return the checked Worker PID.");
            try { HostMaintenanceClient.Prepare(process.Id, Guid.NewGuid().ToString("N")); throw new Exception("Foreign update stole maintenance."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Another local update")) { }
            try { HostMaintenanceClient.Resume(process.Id, Guid.NewGuid().ToString("N")); throw new Exception("Foreign update resumed maintenance."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("does not own")) { }
            using var denied = await Send2026ToolCallAsync(http, endpoint, 903, "zemax_status", "maintenance", "m1");
            Assert((await ReadFirstMcpPayloadAsync(denied)).Contains("service_maintenance"), "New invocation raced an acknowledged idle shutdown.");
            using var listing = await Send2026ListToolsAsync(http, endpoint, 904, "maintenance", "m1");
            Assert(listing.IsSuccessStatusCode, "Static discovery stopped during maintenance.");
            HostMaintenanceClient.Resume(process.Id);
            using var resumed = await Send2026ToolCallAsync(http, endpoint, 905, "zemax_status", "maintenance", "m1");
            Assert((await ReadFirstMcpPayloadAsync(resumed)).Contains("echo-ok"), "Failed update could not resume the old service.");
            Assert(await RunUpdaterAsync() == 0, "Actual net48 updater could not safely replace an idle installation.");
            Assert(File.ReadAllText(Path.Combine(install, "Start-Zemax-MCP.exe")) == "new-test-launcher", "Safe update did not replace the test launcher.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            for (var i = 0; i < 20; i++)
                try { Directory.Delete(directory, true); break; } catch (IOException) when (i < 19) { await Task.Delay(100); }
        }
    }
}
