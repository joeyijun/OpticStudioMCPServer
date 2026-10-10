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
using ZemaxMCP.Server.Tools.Catalog;
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
            VerifyJobsDeltaOwnership();
            VerifyStructuredToolOutcomes();
            VerifyOfficialTasksDefaults();
            VerifyTlsOptions();
            VerifyTaskPlanningCatalog();
            VerifyOriginBoundary();
            await VerifyBackgroundJobLeaseRetentionAsync().ConfigureAwait(false);
            await VerifyCancelledLeaseWaitAsync().ConfigureAwait(false);
            ScopedCredentialAssertions.Verify();
            VerifyJobOwnershipRegistry();
            WorkerTaskLedgerAssertions.Verify();
            VerifyStrictArgumentBinding();
            await VerifyWriteGateCancellationClassificationAsync().ConfigureAwait(false);
            await VerifyContractMismatchRejectedAsync().ConfigureAwait(false);
            await VerifyPipeFaultRecoveryAsync().ConfigureAwait(false);
            await VerifyHardTimeoutRecoveryAsync().ConfigureAwait(false);
            await VerifyNonCooperativeToolHardRecoveryAsync().ConfigureAwait(false);
            await VerifyClientCancellationRecoveryBarrierAsync().ConfigureAwait(false);
            await VerifyProgressEventDispatchAsync().ConfigureAwait(false);
            await VerifyMcpHttpToWorkerEndToEndAsync().ConfigureAwait(false);
            await VerifyScopedCredentialHttpAsync().ConfigureAwait(false);
            Console.WriteLine("Private RPC v3 contract negotiation, recovery, Task ledger, event dispatch, static discovery, identity, Origin, and MCP HTTP E2E verification passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void VerifyTaskPlanningCatalog()
    {
        var previous = Environment.GetEnvironmentVariable("ZEMAX_MCP_TOOLSET");
        var previousReadOnly = Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY");
        try
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_TOOLSET", "basic-viewing");
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", "1");
            var catalog = new ToolCatalogTool();
            // All seven engineering workflows begin with a single bounded,
            // profile-visible model preflight rather than dumping the LDE.
            foreach (var intent in new[] {
                "clipping", "imaging", "straylight", "energy",
                "optimize", "tolerance", "safe-edit" })
            {
                var playbook = catalog.Execute(task: intent).Playbooks.Single();
                Assert(playbook.AvailableSteps.Concat(playbook.UnavailableSteps).FirstOrDefault() ==
                           "zemax_system_summary" &&
                       playbook.AvailableSteps.Contains("zemax_system_summary"),
                    "Task plan must first expose bounded model metadata for " + intent);
            }
            var clipping = catalog.Execute(task: "clipping");
            Assert(clipping.Playbooks.Count == 1 && clipping.Playbooks[0].Id == "clipping",
                "Task planner should return the requested focused playbook.");
            Assert(clipping.Playbooks[0].AvailableSteps.Contains("zemax_ray_trace_diagnostics") &&
                   clipping.Playbooks[0].UnavailableSteps.Contains("zemax_aperture_throughput"),
                "Task planner must identify both available and omitted operations for a narrow profile.");
            Assert(clipping.Tools.All(entry => clipping.Playbooks[0].AvailableSteps.Contains(entry.Name)),
                "Task-specific catalog must not advertise unrelated tools.");

            Environment.SetEnvironmentVariable("ZEMAX_MCP_TOOLSET", "nonsequential-stray-light");
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", "0");
            var energy = catalog.Execute(task: "energy");
            Assert(energy.Playbooks[0].AvailableSteps.Contains("zemax_get_nsc_detector") &&
                   energy.Playbooks[0].AvailableSteps.Contains("zemax_nsc_energy_budget"),
                "NSC energy playbook must include detector data and bounded budget tools.");
            AssertThrows<ArgumentException>(
                () => catalog.Execute(task: "invented-task"),
                "Unknown AI playbook names must fail rather than silently selecting a broad catalog.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_TOOLSET", previous);
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", previousReadOnly);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void VerifyStructuredToolOutcomes()
    {
        Assert(ToolOutcome.InvalidArgument == "invalid_argument" &&
               ToolOutcome.Classify("Result expired") == "expired" &&
               ToolOutcome.Classify("Job not found") == "not_found" &&
               ToolOutcome.Classify("background Job is active") == "conflict" &&
               ToolOutcome.Classify("RPC transport closed") == "transport_error",
            "Typed tool failure mapping was regressed.");
        var error = ToolOutcome.Failure("conflict", "Background Job is active.");
        var json = System.Text.Json.JsonDocument.Parse(((TextContentBlock)error.Content.Single()).Text);
        Assert(error.IsError == true &&
               json.RootElement.GetProperty("code").GetString() == "conflict" &&
               json.RootElement.GetProperty("success").GetBoolean() == false,
            "Structured conflict envelope must preserve isError.");
        var legacy = new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "{\"success\":false,\"error\":\"expired\",\"detail\":12}" }
            }
        };
        var normalized = ToolOutcome.Normalize(legacy);
        using var doc = JsonDocument.Parse(((TextContentBlock)normalized.Content.Single()).Text);
        Assert(normalized.IsError == true &&
               doc.RootElement.GetProperty("code").GetString() == "expired" &&
               doc.RootElement.GetProperty("detail").GetInt32() == 12,
            "Failed legacy JSON must retain its fields while receiving a machine-readable code.");
        var success = new CallToolResult
        {
            IsError = false,
            Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"ok\":true}" } }
        };
        Assert(ReferenceEquals(ToolOutcome.Normalize(success), success),
            "Legacy successful tool output must remain byte-for-byte unchanged.");
    }

    private static void VerifyOfficialTasksDefaults()
    {
        if (!HostOptions.Parse(Array.Empty<string>()).EnableOfficialTasks ||
            HostOptions.Parse(new[] { "--enable-official-tasks", "false" }).EnableOfficialTasks ||
            !HostOptions.Parse(new[] { "--enable-official-tasks", "true" }).EnableOfficialTasks)
            throw new InvalidOperationException("Official Tasks must default on and respect explicit overrides.");
    }

    private static void VerifyTlsOptions()
    {
        var old = Environment.GetEnvironmentVariable("ZEMAX_TEST_TLS_SECRET");
        var path = Path.Combine(Path.GetTempPath(), "zemax-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
        try
        {
            Assert(!HostOptions.Parse(Array.Empty<string>()).TlsEnabled,
                "Legacy loopback listeners must remain HTTP by default.");
            AssertThrows<ArgumentException>(() => HostOptions.Parse(new[] { "--tls-pfx", path }),
                "Host accepted a missing TLS certificate.");
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(
                false, false, 0, true));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
            File.WriteAllBytes(path, cert.Export(
                System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-password"));
            AssertThrows<ArgumentException>(() => HostOptions.Parse(new[] {
                "--tls-pfx", path, "--tls-password-env", "ZEMAX_TEST_TLS_SECRET" }),
                "Host accepted TLS without the password variable.");
            Environment.SetEnvironmentVariable("ZEMAX_TEST_TLS_SECRET", "test-password");
            var enabled = HostOptions.Parse(new[] {
                "--tls-pfx", path, "--tls-password-env", "ZEMAX_TEST_TLS_SECRET" });
            Assert(enabled.TlsEnabled && enabled.TransportScheme == "https" &&
                   enabled.AllowedOrigins.All(x => x.Scheme == "https"),
                "TLS listener/origin scheme mismatch.");
            using var loaded = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                enabled.TlsPfxPath, Environment.GetEnvironmentVariable(enabled.TlsPasswordEnvironmentVariable),
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            Assert(loaded.HasPrivateKey, "Configured TLS certificate must load with a private key.");
            AssertThrows<ArgumentException>(() => HostOptions.Parse(new[] {
                "--tls-password-env", "ZEMAX_TEST_TLS_SECRET" }),
                "Host accepted TLS password option without --tls-pfx.");
            AssertThrows<ArgumentException>(() => HostOptions.Parse(new[] {
                "--tls-pfx", path, "--tls-password-env", "bad name" }),
                "Invalid TLS password environment variable name was accepted.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEMAX_TEST_TLS_SECRET", old);
            if (File.Exists(path)) File.Delete(path);
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
        var filtered = JobOwnerRegistry.FilterList(mixed, "scoped:a", 7, registry, 1);
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
        if (JobOwnerRegistry.FilterList(invalid, "scoped:a", 7, registry, 50).IsError != true)
            throw new InvalidOperationException("Malformed Worker job-list data did not fail closed.");
        var wrongSingle = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"jobId\":\"owned-2\",\"result\":\"client-b-private\"}" } },
            IsError = false
        };
        if (JobOwnerRegistry.ValidateSingleResult(wrongSingle, "owned-1").IsError != true)
            throw new InvalidOperationException("Job status/cancel returned a different owner's result despite authorized request parameters.");

        // An explicit terminal Worker reply must clear a matching lease even
        // when a separate progress event has not reached the Host yet.
        var terminalCancel = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "{\"jobId\":\"owned-1\",\"state\":\"Cancelled\"}" }
            },
            IsError = false
        };
        if (!JobOwnerRegistry.TryGetTerminalState(terminalCancel, "owned-1", out var terminalState) ||
            terminalState != "Cancelled" ||
            JobOwnerRegistry.TryGetTerminalState(terminalCancel, "owned-2", out _))
            throw new InvalidOperationException("Host accepted a missing/foreign terminal Job ID.");
        var pendingJob = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "{\"jobId\":\"owned-1\",\"state\":\"Cancelling\"}" }
            },
            IsError = false
        };
        if (JobOwnerRegistry.TryGetTerminalState(pendingJob, "owned-1", out _) ||
            JobOwnerRegistry.TryGetTerminalState(wrongSingle, "owned-1", out _))
            throw new InvalidOperationException("Nonterminal/foreign Job reply released an optical job lease.");

        var defaultList = new CallToolRequestParams
        {
            Name = "zemax_job_list",
            Arguments = new Dictionary<string, JsonElement>()
        };
        if (!JobOwnerRegistry.TryGetRequestedListLimit(defaultList, out var defaultLimit) ||
            defaultLimit != 50 ||
            JobOwnerRegistry.ExpandListRequest(defaultList).Arguments!["limit"].GetInt32() != 193)
            throw new InvalidOperationException("Scoped Job listing must fetch the whole bounded Worker history.");
        var invalidLimit = new CallToolRequestParams
        {
            Name = "zemax_job_list",
            Arguments = new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(500) }
        };
        if (JobOwnerRegistry.TryGetRequestedListLimit(invalidLimit, out _))
            throw new InvalidOperationException("Scoped Job list incorrectly accepted an invalid public limit.");

        registry.ReleaseGeneration(7);
        if (registry.IsOwned("scoped:a", "owned-1", 7))
            throw new InvalidOperationException("Worker generation change did not revoke old Job IDs.");

        for (var index = 0; index < JobOwnerRegistry.MaximumRecords + 10; index++)
            registry.Register("scoped:a", "bounded-" + index, 8);
        if (registry.IsOwned("scoped:a", "bounded-0", 8) ||
            !registry.IsOwned("scoped:a", "bounded-" + (JobOwnerRegistry.MaximumRecords + 9), 8))
            throw new InvalidOperationException("Bounded job owner history did not drop stale entries safely.");
    }

    private static void VerifyJobsDeltaOwnership()
    {
        var monitor = new McpJobTaskDeltaMonitor();
        var completed = new WorkerJobStatus { JobId="same",State="Completed",ToolName="zemax_run_nsc_ray_trace" };
        var delayed = new WorkerJobStatus { JobId="same",State="Running",ToolName="zemax_run_nsc_ray_trace" };
        var terminalMerge=McpJobTaskDeltaMonitor.MergeStatuses(
            new[]{completed},new[]{delayed});
        Assert(terminalMerge.Single().State=="Completed",
            "Late Worker progress event downgraded a confirmed terminal Job.");
        var eventMerge=McpJobTaskDeltaMonitor.MergeStatuses(
            new[]{delayed},new[]{completed});
        Assert(eventMerge.Single().State=="Completed",
            "Terminal Worker event did not override a stale cached Running state.");
        var jobs = new[] {
            new WorkerJobStatus { JobId="job-alice",ToolName="zemax_run_nsc_ray_trace",State="Running",Fraction=0.25,Message="alice-private" },
            new WorkerJobStatus { JobId="job-bob",ToolName="zemax_global_search",State="Completed",Message="bob-private" }
        };
        var otherOwnerTasks = new object[] { new { taskId="task-alice-private",jobId="job-alice" } };
        var alice = monitor.GetDelta("token:scoped:alice",true,null,17,false,true,
            jobs,otherOwnerTasks,id=>id=="job-alice");
        var encoded = JsonSerializer.Serialize(alice);
        Assert(alice.Changed && alice.Snapshot?.OwnerScoped==true && alice.Snapshot.Jobs.Count==1 &&
               encoded.Contains("alice-private",StringComparison.Ordinal) &&
               !encoded.Contains("job-bob",StringComparison.Ordinal) &&
               !encoded.Contains("bob-private",StringComparison.Ordinal),
            "Jobs delta must return only the authenticated owner's Job and Task data.");
        var unchanged = monitor.GetDelta("token:scoped:alice",true,alice.Cursor,17,false,true,
            jobs,otherOwnerTasks,id=>id=="job-alice");
        Assert(!unchanged.Changed && unchanged.Snapshot==null,
            "No-change scoped Jobs delta must suppress its body.");
        var other = monitor.GetDelta("token:scoped:bob",true,alice.Cursor,17,false,true,
            jobs,Array.Empty<object>(),id=>id=="job-bob");
        Assert(other.Cursor!=alice.Cursor && other.Changed &&
               other.Snapshot?.Jobs.Single().JobId=="job-bob" &&
               !JsonSerializer.Serialize(other).Contains("task-alice-private",StringComparison.Ordinal),
            "Owner cursors or tasks leak between scoped bearer credentials.");
        jobs[1].State="Running";jobs[1].Fraction=0.9;
        Assert(!monitor.GetDelta("token:scoped:alice",true,alice.Cursor,17,false,true,
            jobs,otherOwnerTasks,id=>id=="job-alice").Changed,
            "Foreign Job progress changed Alice's private cursor.");
        jobs[0].Fraction=0.5;
        Assert(monitor.GetDelta("token:scoped:alice",true,alice.Cursor,17,false,true,
            jobs,otherOwnerTasks,id=>id=="job-alice").Changed,
            "Own Worker progress did not advance the owner's delta cursor.");
        Assert(monitor.GetDelta("token:scoped:alice",true,alice.Cursor,18,false,false,
            Array.Empty<WorkerJobStatus>(),Array.Empty<object>(),_=>false).Changed,
            "Worker restart must invalidate stale generation-bound cursors.");
        var anon = monitor.GetDelta("",false,null,17,false,true,jobs,
            otherOwnerTasks,_=>true);
        Assert(anon.Snapshot?.OwnerScoped==false && anon.Snapshot.Jobs.Count==2 && anon.Snapshot.Tasks.Count==0,
            "Shared/local Job state must not expose official Task IDs.");
        AssertThrows<InvalidOperationException>(()=>monitor.GetDelta("",true,null,17,false,true,
            jobs,Array.Empty<object>(),_=>false),"A scoped missing identity was not denied.");
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
        var oldCursor = monitor.GetDelta("client:codex@1.0|remote:192.168.8.20", null, true).Cursor;
        using (monitor.Begin("client:third@1.0", "zemax_status")) { }
        var untouched = monitor.GetDelta("client:codex@1.0|remote:192.168.8.20", oldCursor, true);
        if (untouched.Changed || untouched.Activity != null)
            throw new InvalidOperationException("Scoped cursor reveals changes to a foreign client.");
        first.Dispose();
        var changed = monitor.GetDelta("client:codex@1.0|remote:192.168.8.20", oldCursor, true);
        if (!changed.Changed || changed.Activity?.ActiveRequests != 0 ||
            changed.Cursor == oldCursor)
            throw new InvalidOperationException("Own activity completion was not surfaced by the delta cursor.");
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
        Task<IDisposable> waitingObservation;
        using (await lease.AcquireAsync("client-a", "zemax_global_search", CancellationToken.None).ConfigureAwait(false))
        {
            waitingObservation = lease.AcquireObservationAsync("client-b", CancellationToken.None);
            Assert(!waitingObservation.IsCompleted,
                "An observation interleaved with an in-flight mutation instead of waiting for the shared gate.");
        }
        using (await waitingObservation.ConfigureAwait(false)) { }
        using (await lease.AcquireObservationAsync("client-b", CancellationToken.None).ConfigureAwait(false)) { }
        if (!JsonSerializer.Serialize(lease.GetHealth()).Contains("client-a", StringComparison.Ordinal))
            throw new InvalidOperationException("Foreign observation stole or cleared the persistent write owner.");
        if (!lease.RetainForJob("client-a", "job-1", generation: 7))
            throw new InvalidOperationException("The owning client could not retain control for its background job.");

        // The original owner must NOT start another mutation while its own
        // background COM Job is active; serialization of RPC calls alone
        // cannot guarantee that a started Job has finished.
        try
        {
            using var _ = await lease.AcquireAsync("client-a", "zemax_set_surface", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("The owning client mutated the model while its background Job remained active.");
        }
        catch (ControlLeaseConflictException ex)
        {
            if (!ex.Message.Contains("background Job", StringComparison.Ordinal))
                throw new InvalidOperationException("Own-job mutation conflict must explain how to recover.", ex);
        }

        await Task.Delay(80).ConfigureAwait(false);
        try
        {
            using var _ = await lease.AcquireObservationAsync("client-b", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Foreign observation entered during an active optical Job.");
        }
        catch (ControlLeaseConflictException) { }
        try
        {
            using var _ = await lease.AcquireAsync("client-b", "zemax_status", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A background job did not prevent lease expiry and cross-client takeover.");
        }
        catch (ControlLeaseConflictException) { }

        lease.ObserveJob(8, new WorkerJobStatus { JobId = "job-1", State = "Completed" });
        await Task.Delay(80).ConfigureAwait(false);
        try
        {
            using var _ = await lease.AcquireAsync("client-b", "zemax_status", CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A terminal event from the wrong Worker generation released the job lease.");
        }
        catch (ControlLeaseConflictException) { }

        lease.ReleaseGeneration(7);
        // Dead Worker generations must relinquish idle background ownership
        // immediately, not after the fifteen-minute inactivity period.
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

    private static async Task VerifyNonCooperativeToolHardRecoveryAsync()
    {
        // This is a deterministic RPC/Worker-process fault injection. The fake
        // Worker never responds to cancellation; it is not an actual ZOS COM
        // invocation. It proves the Host's hard timer kills that generation.
        Environment.SetEnvironmentVariable("ZEMAX_MCP_FAKE_WORKER_MODE", null);
        await using var client = new WorkerRpcClient(CreateOptions(10, 20));
        var ready = await client.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        if (!ready.Connected || !client.TryGetCachedStatus(out var cached) ||
            !string.Equals(cached?.ManifestFingerprint, StaticToolManifest.ContractFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Last verified Worker status was not cached for nonblocking diagnostics.");
        if (client.LastValidatedStatusAt is not { } timestamp ||
            DateTimeOffset.UtcNow - timestamp > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Fresh fake Worker status lacked a recent validation timestamp.");
        var before = client.CurrentGeneration;
        var timer = Stopwatch.StartNew();
        try
        {
            await client.CallToolAsync(TestTool("zemax_test_hang"), CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("A non-cooperative Worker tool unexpectedly completed.");
        }
        catch (TimeoutException) { }

        timer.Stop();
        if (timer.Elapsed < TimeSpan.FromSeconds(10) || timer.Elapsed > TimeSpan.FromSeconds(26))
            throw new InvalidOperationException("Hard recovery did not respect its configured soft/hard timeout window.");

        if (client.TryGetCachedStatus(out _) || client.LastValidatedStatusAt.HasValue)
            throw new InvalidOperationException("Retired Worker generation leaked its cached license/connection timestamp.");
        var result = await client.CallToolAsync(TestTool("zemax_test_echo"), CancellationToken.None).ConfigureAwait(false);
        if (result.IsError == true || client.CurrentGeneration <= before)
            throw new InvalidOperationException("A non-cooperative fake Worker was not replaced by a newer, responsive generation.");
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

            using (var diagnosticHealth = JsonDocument.Parse(healthBody))
            {
                if (diagnosticHealth.RootElement.GetProperty("controlLease").GetProperty("owner").ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("A Launcher status check claimed optical ownership.");
            }
            foreach (var diagnostic in new[] { "zemax_status", "zemax_tool_catalog" })
            {
                using var secondLauncher = await Send2026ToolCallAsync(client, endpoint, 301, diagnostic, "launcher-b", "instance-b").ConfigureAwait(false);
                var body = await ReadFirstMcpPayloadAsync(secondLauncher).ConfigureAwait(false);
                if (!body.Contains("echo-ok", StringComparison.Ordinal) || body.Contains("isError\":true", StringComparison.Ordinal))
                    throw new InvalidOperationException("Two Launcher metadata probes could not coexist: " + body);
            }

            // The shared-token Host is explicitly --read-only true.
            // Keep this fixture read-only; the scoped read-write fixture
            // below exercises distinct same-name same-IP writer identities,
            // exclusive modification and post-disconnect handoff.
            // Start the slow Worker read without awaiting the HTTP headers,
            // then observe the real in-flight activity before completion.
            var heldResponseTask = Send2026ToolCallAsync(client, endpoint, 4,
                "zemax_get_system", "client-a", "instance-a");
            var sawActiveRead = false;
            var observedActivity = "";
            for (var attempt = 0; attempt < 16 && !sawActiveRead; attempt++)
            {
                await Task.Delay(125).ConfigureAwait(false);
                using var activityRequest = new HttpRequestMessage(HttpMethod.Get, endpoint + "/activity");
                activityRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
                using var activityResponse = await client.SendAsync(activityRequest).ConfigureAwait(false);
                using var activityJson = JsonDocument.Parse(
                    await activityResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                observedActivity = activityJson.RootElement.GetRawText();
                if (!activityResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException("Activity endpoint was not reachable during read.");
                sawActiveRead = activityJson.RootElement.GetProperty("activeOperations")
                    .EnumerateArray().Any(item =>
                        item.GetProperty("client").GetString()?.Contains("client-a", StringComparison.Ordinal) == true &&
                        item.GetProperty("tool").GetString() == "zemax_get_system");
            }
            if (!sawActiveRead)
            {
                var responseText = heldResponseTask.IsCompleted
                    ? " Early MCP response: " + await ReadFirstMcpPayloadAsync(
                        await heldResponseTask.ConfigureAwait(false)).ConfigureAwait(false)
                    : " Worker request is still pending.";
                throw new InvalidOperationException(
                    "In-flight read was not observable on /activity: " + observedActivity + responseText);
            }
            // A health check during a long optical read must return promptly
            // from the last validated Worker status instead of entering the
            // same blocked STA/RPC queue, which could exhaust the long-job
            // timeout or make the Launcher appear frozen.
            using (var busyHealth = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health"))
            {
                busyHealth.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
                using var busyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                using var busyResponse = await client.SendAsync(busyHealth, busyDeadline.Token).ConfigureAwait(false);
                using var snapshot = JsonDocument.Parse(
                    await busyResponse.Content.ReadAsStringAsync(busyDeadline.Token).ConfigureAwait(false));
                if (!busyResponse.IsSuccessStatusCode ||
                    snapshot.RootElement.GetProperty("workerBusy").GetBoolean() != true ||
                    snapshot.RootElement.GetProperty("statusFresh").GetBoolean() != false ||
                    !snapshot.RootElement.GetProperty("lastKnownStatus").GetBoolean() ||
                    snapshot.RootElement.GetProperty("statusValidatedAt").ValueKind != JsonValueKind.String ||
                    snapshot.RootElement.GetProperty("statusAgeSeconds").GetDouble() < 0 ||
                    snapshot.RootElement.GetProperty("licenseStatus").GetString() != "fake-license")
                    throw new InvalidOperationException(
                        "Busy Worker health did not report accurate cached/stale status promptly: " +
                        snapshot.RootElement.GetRawText());
            }

            using (var heldResponse = await heldResponseTask.ConfigureAwait(false))
            {
                var heldBody = await ReadFirstMcpPayloadAsync(heldResponse).ConfigureAwait(false);
                if (!heldResponse.IsSuccessStatusCode || !heldBody.Contains("echo-ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("Read-only Worker request failed: " + heldBody);
            }

            using (var foreignRead = await Send2026ToolCallAsync(client, endpoint, 5,
                       "zemax_get_system", "client-a", "instance-b").ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(foreignRead).ConfigureAwait(false);
                if (!foreignRead.IsSuccessStatusCode || !body.Contains("echo-ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("Separate read-only instances could not inspect one model: " + body);
            }
            using (var healthCheck = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health"))
            {
                healthCheck.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-rpc-e2e-token");
                using var healthResult = await client.SendAsync(healthCheck).ConfigureAwait(false);
                using var snapshot = JsonDocument.Parse(
                    await healthResult.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (!healthResult.IsSuccessStatusCode ||
                    snapshot.RootElement.GetProperty("controlLease").GetProperty("owner").ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("Non-mutating reads must not acquire a persistent writer lease.");
            }

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
            $"--client-credentials-file \"{file}\" --allowed-host 127.0.0.1 --allowed-origin http://127.0.0.1:* --enable-official-tasks true")
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
                    System.Text.RegularExpressions.Regex.IsMatch(body, "\"name\"\\s*:\\s*\"zemax_multistart_status\"") ||
                    System.Text.RegularExpressions.Regex.IsMatch(body, "\"name\"\\s*:\\s*\"zemax_multistart_stop\""))
                    throw new InvalidOperationException("Write credential did not enforce modern Job-only access and authorized tools.");
            }

            using (var run = await SendScopedAsync(client, endpoint, 104, "tools/call", "zemax_connect", writer).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(run).ConfigureAwait(false);
                if (!run.IsSuccessStatusCode || !body.Contains("echo-ok", StringComparison.Ordinal) ||
                    !File.Exists(workerLog))
                {
                    // The MCP SDK deliberately returns a generic external
                    // invocation error. Surface only ERROR-level local Host
                    // diagnostics in this headless fake-Worker CI test so the
                    // real exception can be fixed without weakening policy.
                    var log = Directory.GetFiles(testRoot, "http-host-*.log")
                        .OrderBy(value => value, StringComparer.Ordinal).LastOrDefault();
                    var hostErrors = "(Host log unavailable)";
                    if (log != null)
                    {
                        try
                        {
                            using var diagnosticStream = new FileStream(log, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            using var diagnosticReader = new StreamReader(diagnosticStream);
                            var diagnosticLines = diagnosticReader.ReadToEnd()
                                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                            var lastError = Array.FindLastIndex(diagnosticLines, line =>
                                line.Contains("[ERR]", StringComparison.Ordinal) ||
                                line.Contains("[FTL]", StringComparison.Ordinal));
                            hostErrors = lastError < 0 ? "(no error lines)" :
                                string.Join(" | ", diagnosticLines.Skip(lastError).Take(14))
                                    .Substring(0, Math.Min(3800, string.Join(" | ",
                                        diagnosticLines.Skip(lastError).Take(14)).Length));
                        }
                        catch (IOException) { hostErrors = "(Host log locked by active process)"; }
                    }
                    throw new InvalidOperationException("Scoped writer did not reach the Worker: " +
                        body + " Host errors: " + hostErrors);
                }
            }

            // Both clients deliberately advertise the same clientInfo and
            // instance ID. The authenticated token ID must own the lease.
            using (var competing = await SendScopedAsync(client, endpoint, 105, "tools/call", "zemax_connect", otherWriter).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(competing).ConfigureAwait(false);
                if (body.Contains("echo-ok", StringComparison.Ordinal) ||
                    (!body.Contains("currently leased", StringComparison.OrdinalIgnoreCase) &&
                     !body.Contains("isError", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Two distinct authenticated tokens shared one control lease: " + body);
            }

            using (var readerObservation = await SendScopedAsync(client, endpoint, 1051,
                       "tools/call", "zemax_get_system", reader).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(readerObservation).ConfigureAwait(false);
                if (!readerObservation.IsSuccessStatusCode ||
                    !body.Contains("echo-ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("Read-only credential cannot inspect model owned by another token: " + body);
            }

            using (var release = await SendScopedAsync(client, endpoint, 106, "tools/call", "zemax_disconnect", writer).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(release).ConfigureAwait(false);
                if (!release.IsSuccessStatusCode || !body.Contains("success", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Scoped owner could not release its lease.");
            }
            using (var handoff = await SendScopedAsync(client, endpoint, 107, "tools/call", "zemax_connect", otherWriter).ConfigureAwait(false))
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
            // The Launcher consumes /health and /activity. Those endpoints
            // must obey exactly the same scoped ownership confidentiality as
            // explicit Job status/list and official Tasks.
            foreach (var route in new[] { "/health", "/activity" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + route);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", writer);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode ||
                    body.Contains("fake-owned-job", StringComparison.Ordinal) ||
                    body.Contains("token:scoped:writer-two", StringComparison.Ordinal) ||
                    body.Contains("zemax_run_nsc_ray_trace", StringComparison.Ordinal) ||
                    body.Contains("private-job-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped diagnostic endpoint exposed a foreign background Job: " + route);
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
            using (var scopedHealth = await SendScopedGetAsync(client, new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') + "/health"), otherWriter).ConfigureAwait(false))
            {
                var body = await scopedHealth.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!scopedHealth.IsSuccessStatusCode || !body.Contains("scoped", StringComparison.Ordinal) ||
                    !body.Contains("zosApiConnected", StringComparison.Ordinal) ||
                    !body.Contains("\"jobs\"", StringComparison.Ordinal) ||
                    !body.Contains("\"tasks\"", StringComparison.Ordinal) ||
                    body.Contains("private-job-result", StringComparison.Ordinal) ||
                    body.Contains("eventJobs", StringComparison.Ordinal) ||
                    body.Contains("C:\\\\Fake", StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped /health must expose safe owner-filtered status, not cross-client results or paths.");
            }
            using (var scopedActivity = await SendScopedGetAsync(client, new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') + "/activity"), writer).ConfigureAwait(false))
            {
                var body = await scopedActivity.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!scopedActivity.IsSuccessStatusCode || body.Contains("fake-owned-job", StringComparison.Ordinal) ||
                    body.Contains("activeOperations", StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped /activity leaked cross-client operations.");
            }
            // Delta cursors are stable for one owner unless that owner's
            // activity changes. A second scoped credential cannot read
            // foreign active-operation names or results through this route.
            string ownerActivityCursor;
            using (var delta = await SendScopedGetAsync(client,
                       new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') + "/activity-delta"),
                       otherWriter).ConfigureAwait(false))
            {
                var body = await delta.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var parsed = JsonDocument.Parse(body);
                ownerActivityCursor = parsed.RootElement.GetProperty("cursor").GetString() ?? "";
                if (!delta.IsSuccessStatusCode || ownerActivityCursor.Length != 64 ||
                    !parsed.RootElement.GetProperty("changed").GetBoolean() ||
                    body.Contains("private-unknown-result", StringComparison.Ordinal) ||
                    body.Contains("private-job-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped activity cursor leaked Job data or failed to initialize: " + body);
            }
            using (var unchanged = await SendScopedGetAsync(client,
                       new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') +
                           "/activity-delta?cursor=" + ownerActivityCursor), otherWriter).ConfigureAwait(false))
            {
                var body = await unchanged.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var parsed = JsonDocument.Parse(body);
                if (!unchanged.IsSuccessStatusCode ||
                    parsed.RootElement.GetProperty("changed").GetBoolean() ||
                    parsed.RootElement.GetProperty("activity").ValueKind != JsonValueKind.Null)
                    throw new InvalidOperationException("Activity delta did not suppress unchanged owner data: " + body);
            }

            string ownerJobCursor;
            using (var firstJobs = await SendScopedGetAsync(client,
                       new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') + "/jobs-delta"),
                       otherWriter).ConfigureAwait(false))
            {
                var body=await firstJobs.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var parsed=JsonDocument.Parse(body);
                var jobDeltaRoot=parsed.RootElement;
                ownerJobCursor=jobDeltaRoot.GetProperty("cursor").GetString() ?? "";
                if(!firstJobs.IsSuccessStatusCode || ownerJobCursor.Length!=64 ||
                    !jobDeltaRoot.GetProperty("changed").GetBoolean() ||
                    !body.Contains("fake-owned-job",StringComparison.Ordinal) ||
                    body.Contains("private-unknown-result",StringComparison.Ordinal) ||
                    body.Contains("private-job-result",StringComparison.Ordinal) ||
                    body.Contains("C:\\\\Fake",StringComparison.Ordinal))
                    throw new InvalidOperationException("Scoped Jobs delta failed to filter secrets: "+body);
            }
            using (var noJobsChange=await SendScopedGetAsync(client,
                       new Uri(endpoint,endpoint.AbsolutePath.TrimEnd('/') +
                           "/jobs-delta?cursor="+ownerJobCursor),otherWriter).ConfigureAwait(false))
            {
                var body=await noJobsChange.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var parsed=JsonDocument.Parse(body);
                if(!noJobsChange.IsSuccessStatusCode || parsed.RootElement.GetProperty("changed").GetBoolean() ||
                   parsed.RootElement.GetProperty("snapshot").ValueKind!=JsonValueKind.Null)
                    throw new InvalidOperationException("Jobs delta repeated identical owned data: "+body);
            }
            using (var foreignJobs=await SendScopedGetAsync(client,
                       new Uri(endpoint,endpoint.AbsolutePath.TrimEnd('/') + "/jobs-delta"),writer).ConfigureAwait(false))
            {
                var body=await foreignJobs.Content.ReadAsStringAsync().ConfigureAwait(false);
                if(!foreignJobs.IsSuccessStatusCode ||
                   body.Contains("fake-owned-job",StringComparison.Ordinal) ||
                   body.Contains("private-job-result",StringComparison.Ordinal))
                    throw new InvalidOperationException("Foreign credential saw owner jobs in delta: "+body);
            }

            using (var ownerCancel = await SendScopedAsync(client, endpoint, 114, "tools/call", "zemax_job_cancel", otherWriter,
                       new { jobId = "fake-owned-job" }).ConfigureAwait(false))
            {
                var body = await ReadFirstMcpPayloadAsync(ownerCancel).ConfigureAwait(false);
                if (!ownerCancel.IsSuccessStatusCode || !body.Contains("Cancelled", StringComparison.Ordinal))
                    throw new InvalidOperationException("Job creator could not cancel its own Job: " + body);
            }


            // A synchronous tool never becomes a Task merely because the
            // client advertises Tasks support.
            using (var inline = await SendTaskAsync(client, endpoint, 1141, "tools/call",
                       "zemax_status", otherWriter).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(inline).ConfigureAwait(false);
                if (!inline.IsSuccessStatusCode ||
                    !payload.Contains("echo-ok", StringComparison.Ordinal) ||
                    payload.Contains("\"resultType\":\"task\"", StringComparison.Ordinal))
                    throw new InvalidOperationException("Opt-in changed synchronous tool semantics: " + payload);
            }

            // Real 2026-07-28 Tasks protocol E2E. Identical spoofable client
            // metadata must never override the independently authenticated owner.
            string taskId;
            using (var created = await SendTaskAsync(client, endpoint, 115, "tools/call",
                       "zemax_run_nsc_ray_trace", otherWriter).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(created).ConfigureAwait(false);
                using var json = JsonDocument.Parse(payload);
                if (!created.IsSuccessStatusCode ||
                    json.RootElement.GetProperty("result").GetProperty("resultType").GetString() != "task")
                    throw new InvalidOperationException("Opted-in NSC tool did not return official CreateTaskResult: " + payload);
                taskId = json.RootElement.GetProperty("result").GetProperty("taskId").GetString()!;
                if (string.IsNullOrWhiteSpace(taskId))
                    throw new InvalidOperationException("Official CreateTaskResult did not have a taskId.");
            }

            // Scoped diagnostics must show the owner's official Task handle
            // while disclosing neither this Task nor Worker paths to a foreign token.
            foreach (var (bearer, shouldSeeOwn) in new[] {
                (otherWriter, true), (writer, false)
            })
            {
                using var response = await SendScopedGetAsync(client,
                    new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') + "/health"), bearer).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode ||
                    payload.Contains(taskId, StringComparison.Ordinal) != shouldSeeOwn ||
                    payload.Contains("fake-task-job", StringComparison.Ordinal) != shouldSeeOwn ||
                    payload.Contains("C:\\\\Fake", StringComparison.Ordinal) ||
                    payload.Contains("private-task-final-result", StringComparison.Ordinal) ||
                    payload.Contains("eventJobs", StringComparison.Ordinal))
                    throw new InvalidOperationException("Owner-scoped Task diagnostic listing is incorrect.");
            }

            foreach (var method in new[] { "tasks/get", "tasks/update", "tasks/cancel" })
            {
                using var deniedTask = await SendTaskAsync(client, endpoint, 116, method, null, writer, taskId).ConfigureAwait(false);
                var payload = await ReadFirstMcpPayloadAsync(deniedTask).ConfigureAwait(false);
                if (!payload.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    payload.Contains("private-task-final-result", StringComparison.Ordinal) ||
                    payload.Contains("fake-task-job", StringComparison.Ordinal))
                    throw new InvalidOperationException("A second authenticated client crossed the " + method + " Task boundary: " + payload);
            }

            using (var missingCapability = await SendTaskAsync(client, endpoint, 117, "tasks/get", null,
                       otherWriter, taskId, capability: false).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(missingCapability).ConfigureAwait(false);
                if (!payload.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Task polling without per-call opt-in was accepted.");
            }


            using (var emptyUpdate = await SendTaskAsync(client, endpoint, 1171, "tasks/update", null,
                       otherWriter, taskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(emptyUpdate).ConfigureAwait(false);
                if (!emptyUpdate.IsSuccessStatusCode || payload.Contains("\"error\":", StringComparison.Ordinal))
                    throw new InvalidOperationException("Owner's empty Task update should be acknowledged: " + payload);
            }
            using (var unsolicited = await SendTaskAsync(client, endpoint, 1172, "tasks/update", null,
                       otherWriter, taskId, inputResponsesJson: "{\"unknown\":{\"result\":{}}}").ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(unsolicited).ConfigureAwait(false);
                if (!payload.Contains("\"error\":", StringComparison.Ordinal))
                    throw new InvalidOperationException("Worker Job falsely accepted an unsolicited Task input response.");
            }

            // With a long COM-bound foreground read in progress, tasks/get
            // must use the Host's already-authorized Task state rather than
            // queuing job_status behind the busy Worker RPC. This is a real
            // concurrent HTTP smoke test, not just a static code assertion.
            var longModelRead = SendScopedAsync(client, endpoint, 1173, "tools/call",
                "zemax_get_system", otherWriter);
            var workerBusySeen = false;
            for (var poll = 0; poll < 20 && !workerBusySeen; poll++)
            {
                await Task.Delay(75).ConfigureAwait(false);
                using var busyHealth = await SendScopedGetAsync(client,
                    new Uri(endpoint, endpoint.AbsolutePath.TrimEnd('/') + "/health"),
                    otherWriter).ConfigureAwait(false);
                using var healthJson = JsonDocument.Parse(
                    await busyHealth.Content.ReadAsStringAsync().ConfigureAwait(false));
                workerBusySeen = busyHealth.IsSuccessStatusCode &&
                    healthJson.RootElement.GetProperty("workerBusy").GetBoolean() &&
                    healthJson.RootElement.GetProperty("foregroundRpcBusy").GetBoolean() &&
                    healthJson.RootElement.GetProperty("activeRequests").GetInt32() > 0 &&
                    string.Equals(healthJson.RootElement.GetProperty("lastTool").GetString(),
                        "zemax_get_system", StringComparison.Ordinal);
            }
            if (!workerBusySeen)
                throw new InvalidOperationException("Scoped long-read fixture never entered Worker busy state.");
            var taskPollTimer = Stopwatch.StartNew();
            using (var nonBlockingPoll = await SendTaskAsync(client, endpoint, 1174, "tasks/get", null,
                       otherWriter, taskId).ConfigureAwait(false))
            {
                taskPollTimer.Stop();
                var payload = await ReadFirstMcpPayloadAsync(nonBlockingPoll).ConfigureAwait(false);
                if (!nonBlockingPoll.IsSuccessStatusCode ||
                    !payload.Contains("\"working\"", StringComparison.Ordinal) ||
                    taskPollTimer.Elapsed > TimeSpan.FromSeconds(2))
                    throw new InvalidOperationException(
                        "tasks/get blocked behind a running Worker COM-bound RPC: " +
                        taskPollTimer.Elapsed.TotalMilliseconds + "ms; " + payload);
            }
            using (var modelRead = await longModelRead.ConfigureAwait(false))
            {
                var result = await ReadFirstMcpPayloadAsync(modelRead).ConfigureAwait(false);
                if (!modelRead.IsSuccessStatusCode || !result.Contains("echo-ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("Long read fixture did not complete: " + result);
            }

            using (var working = await SendTaskAsync(client, endpoint, 118, "tasks/get", null,
                       otherWriter, taskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(working).ConfigureAwait(false);
                if (!payload.Contains("\"working\"", StringComparison.Ordinal) ||
                    payload.Contains("private-task-final-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Task completed before Worker returned its real result: " + payload);
            }
            using (var finished = await SendTaskAsync(client, endpoint, 119, "tasks/get", null,
                       otherWriter, taskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(finished).ConfigureAwait(false);
                if (!payload.Contains("\"completed\"", StringComparison.Ordinal) ||
                    !payload.Contains("private-task-final-result", StringComparison.Ordinal) ||
                    !payload.Contains("result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Task did not expose its actual Worker final result: " + payload);
            }
            using (var lateCancel = await SendTaskAsync(client, endpoint, 120, "tasks/cancel", null,
                       otherWriter, taskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(lateCancel).ConfigureAwait(false);
                if (!lateCancel.IsSuccessStatusCode || payload.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Terminal Task cancellation must be an idempotent acknowledgement.");
            }
            using (var stillCompleted = await SendTaskAsync(client, endpoint, 121, "tasks/get", null,
                       otherWriter, taskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(stillCompleted).ConfigureAwait(false);
                if (!payload.Contains("\"completed\"", StringComparison.Ordinal) ||
                    !payload.Contains("private-task-final-result", StringComparison.Ordinal))
                    throw new InvalidOperationException("Late cancellation overwrote a completed Task.");
            }

            // One more long Job verifies real cancellation rather than merely
            // acknowledging the Task handle.
            string cancelTaskId;
            using (var created = await SendTaskAsync(client, endpoint, 122, "tools/call",
                       "zemax_run_nsc_ray_trace", otherWriter).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(created).ConfigureAwait(false);
                using var json = JsonDocument.Parse(payload);
                cancelTaskId = json.RootElement.GetProperty("result").GetProperty("taskId").GetString()!;
            }
            using (var cancelled = await SendTaskAsync(client, endpoint, 123, "tasks/cancel",
                       null, otherWriter, cancelTaskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(cancelled).ConfigureAwait(false);
                if (!cancelled.IsSuccessStatusCode || payload.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Task cancellation did not forward to Worker Job: " + payload);
            }
            using (var cancelledState = await SendTaskAsync(client, endpoint, 124, "tasks/get",
                       null, otherWriter, cancelTaskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(cancelledState).ConfigureAwait(false);
                if (!payload.Contains("\"cancelled\"", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Worker-confirmed cancellation did not reach Task state: " + payload);
            }

            // A real (fake-process) Worker generation loss must invalidate
            // the active Task, then allow a different authenticated controller
            // to take the newly started Worker immediately.
            string crashedTaskId;
            using (var started = await SendTaskAsync(client, endpoint, 126, "tools/call",
                       "zemax_pop", otherWriter).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(started).ConfigureAwait(false);
                using var json = JsonDocument.Parse(payload);
                if (!started.IsSuccessStatusCode ||
                    json.RootElement.GetProperty("result").GetProperty("resultType").GetString() != "task")
                    throw new InvalidOperationException("Worker-exit fixture did not create a Task: " + payload);
                crashedTaskId = json.RootElement.GetProperty("result").GetProperty("taskId").GetString()!;
            }
            await Task.Delay(2200).ConfigureAwait(false);
            using (var ended = await SendTaskAsync(client, endpoint, 127, "tasks/get",
                       null, otherWriter, crashedTaskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(ended).ConfigureAwait(false);
                if (!ended.IsSuccessStatusCode ||
                    !payload.Contains("\"failed\"", StringComparison.Ordinal) ||
                    !payload.Contains("generation", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Worker generation loss left a Task working or leaked a fake result: " + payload);
            }
            using (var foreignTask = await SendTaskAsync(client, endpoint, 128, "tasks/get",
                       null, writer, crashedTaskId).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(foreignTask).ConfigureAwait(false);
                if (!payload.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Worker recovery exposed a foreign failed Task: " + payload);
            }
            using (var immediateHandoff = await SendScopedAsync(client, endpoint, 129,
                       "tools/call", "zemax_status", writer).ConfigureAwait(false))
            {
                var payload = await ReadFirstMcpPayloadAsync(immediateHandoff).ConfigureAwait(false);
                if (!immediateHandoff.IsSuccessStatusCode ||
                    !payload.Contains("echo-ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("Worker failure retained an idle foreign control lease: " + payload);
            }

            // The Task owner is revoked while its terminal result still exists.
            ScopedCredentialAssertions.Write(file, ("reader", "read-only", reader), ("writer", "read-write", writer));
            using (var revokedOwner = await SendTaskAsync(client, endpoint, 125, "tasks/get",
                       null, otherWriter, taskId).ConfigureAwait(false))
                if (revokedOwner.StatusCode != HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Revoked Task creator could still retrieve its result.");
            ScopedCredentialAssertions.Write(file,
                ("reader", "read-only", reader), ("writer", "read-write", writer),
                ("writer-two", "read-write", otherWriter));

            if (File.ReadAllLines(workerLog).Count(line => line.StartsWith("nsc:", StringComparison.Ordinal)) != 3)
                throw new InvalidOperationException("Task protocol caused an unexpected duplicate Worker NSC execution.");

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


    private static Task<HttpResponseMessage> SendTaskAsync(
        HttpClient client, Uri endpoint, int id, string method, string? toolName,
        string bearer, string? taskId = null, bool capability = true, string? inputResponsesJson = null)
    {
        var body = Build2026Body(id, method, toolName, "spoofable-client-name", "same-instance-id");
        if (capability)
            body = body.Replace("\"io.modelcontextprotocol/clientCapabilities\":{}",
                "\"io.modelcontextprotocol/clientCapabilities\":{\"extensions\":{\"io.modelcontextprotocol/tasks\":{}}}",
                StringComparison.Ordinal);
        if (taskId != null)
            body = body.Replace("\"_meta\":{", "\"taskId\":" + JsonSerializer.Serialize(taskId) + ",\"_meta\":{",
                StringComparison.Ordinal);
        if (inputResponsesJson != null)
            body = body.Replace("\"_meta\":{", "\"inputResponses\":" + inputResponsesJson + ",\"_meta\":{",
                StringComparison.Ordinal);
        var request = Create2026Request(endpoint, body, method, toolName ?? taskId);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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

    private static Task<HttpResponseMessage> Send2026ToolCallAsync(HttpClient client, Uri endpoint, int id, string toolName, string clientName, string instanceId, object? arguments = null)
    {
        var body = Build2026Body(id, "tools/call", toolName, clientName, instanceId);
        if (arguments != null)
            body = body.Replace("\"arguments\":{}", "\"arguments\":" + JsonSerializer.Serialize(arguments), StringComparison.Ordinal);
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
        var nscStarts = 0;
        var completedTaskPolls = 0;
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
                if (string.Equals(command, "zemax_get_system", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_connect", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_test_hold", StringComparison.Ordinal))
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
                if (string.Equals(command, "zemax_pop", StringComparison.Ordinal))
                {
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text",
                            text = "{\"success\":true,\"jobId\":\"fake-crash-task-job\",\"state\":\"Queued\"}" } },
                        isError = false
                    }).ConfigureAwait(false);
                    // A real Worker process exits AFTER the Job handle has
                    // been returned. This exercises generation cleanup, not
                    // the separate cancellation-grace clock.
                    await Task.Delay(1500).ConfigureAwait(false);
                    return;
                }
                if (string.Equals(command, "zemax_run_nsc_ray_trace", StringComparison.Ordinal))
                {
                    var jobId = ++nscStarts == 1 ? "fake-owned-job" : "fake-task-job-" + (nscStarts - 1);
                    if (!string.IsNullOrWhiteSpace(workerLog))
                        File.AppendAllText(workerLog, "nsc:" + jobId + Environment.NewLine);
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text",
                            text = JsonSerializer.Serialize(new { success = true, jobId, state = "Queued" }) } },
                        isError = false
                    }).ConfigureAwait(false);
                    continue;
                }
                if (string.Equals(command, "zemax_job_status", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_job_cancel", StringComparison.Ordinal))
                {
                    var cancelling = string.Equals(command, "zemax_job_cancel", StringComparison.Ordinal);
                    var id = root.GetProperty("payload").GetProperty("arguments").GetProperty("jobId").GetString()
                        ?? string.Empty;
                    string state;
                    object resultValue;
                    if (id == "fake-owned-job")
                    {
                        state = cancelling ? "Cancelled" : "Running";
                        resultValue = "private-job-result";
                    }
                    else if (id == "fake-task-job-1")
                    {
                        state = cancelling ? "Cancelled" : ++completedTaskPolls >= 2 ? "Completed" : "Running";
                        resultValue = new { privateData = "private-task-final-result", raysTraced = 127 };
                    }
                    else
                    {
                        state = cancelling ? "Cancelled" : "Running";
                        resultValue = new { privateData = "private-cancelled-task-result" };
                    }
                    await SendAsync(writer, ZemaxRpcProtocol.Result, requestId, operationId, new
                    {
                        content = new[] { new { type = "text",
                            text = JsonSerializer.Serialize(new { jobId = id, state, resultExpired = false, result = resultValue }) } },
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
                if (string.Equals(command, "zemax_status", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_tool_catalog", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_get_system", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_connect", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_test_echo", StringComparison.Ordinal) ||
                    string.Equals(command, "zemax_test_hold", StringComparison.Ordinal))
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
