using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using ZemaxMCP.HttpBridge.ModernHost;
using ZemaxMCP.Rpc;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.PrivateRpcTests;

internal static partial class Program
{
    private static async Task VerifyExpandedBackgroundExecutionAsync()
    {
        if (BackgroundToolPolicy.EligibleTools.Count != 23) throw new Exception("Background capability count drifted.");
        foreach (var name in BackgroundToolPolicy.EligibleTools)
        {
            var entry = StaticToolManifest.GetRequired(name);
            if (entry.BackgroundExecution == "none") throw new Exception("Missing background execution contract.");
            if (BackgroundToolPolicy.UsesSharedRunner(name))
            {
                var properties = entry.InputSchema.GetProperty("properties");
                if (properties.GetProperty("runInBackground").GetProperty("default").GetBoolean() ||
                    properties.GetProperty("backgroundTimeoutSeconds").GetProperty("maximum").GetInt32() != 7200)
                    throw new Exception("Shared background options must preserve ordinary synchronous compatibility and a finite bound.");
            }
        }
        if (BackgroundToolPolicy.SupportsTasks("zemax_get_surface") ||
            StaticToolManifest.GetRequired("zemax_get_surface").InputSchema.GetProperty("properties").TryGetProperty("runInBackground", out _))
            throw new Exception("Short metadata/edit tools were accidentally promoted to background work.");

        using var input = JsonDocument.Parse("{\"field\":2,\"runInBackground\":true,\"backgroundTimeoutSeconds\":1}");
        var prepared = BackgroundToolPolicy.PrepareSharedInvocation("zemax_fft_mtf", input.RootElement);
        if (!prepared.Background || prepared.TimeoutSeconds != 1 ||
            prepared.Arguments.TryGetProperty("runInBackground", out _) ||
            prepared.Arguments.GetProperty("field").GetInt32() != 2)
            throw new Exception("Virtual background controls must not leak into native method arguments.");
        var method = typeof(BackgroundBindingFixture).GetMethod(nameof(BackgroundBindingFixture.Execute))!;
        var bound = WorkerToolRegistry.BindArguments(method, prepared.Arguments, default);
        if ((int)bound[0]! != 2) throw new Exception("Native reflection binding lost arguments.");
        foreach (var json in new[] { "{\"runInBackground\":\"true\"}", "{\"backgroundTimeoutSeconds\":0}",
                     "{\"backgroundTimeoutSeconds\":7201}", "{\"backgroundTimeoutSeconds\":\"10\"}" })
        {
            using var invalid = JsonDocument.Parse(json);
            try { BackgroundToolPolicy.PrepareSharedInvocation("zemax_fft_mtf", invalid.RootElement); throw new Exception("Invalid control accepted."); }
            catch (ArgumentException) { }
        }
        using var unknown = JsonDocument.Parse("{\"field\":2,\"runInBackground\":true,\"unknownNativeOption\":1}");
        var cleaned = BackgroundToolPolicy.PrepareSharedInvocation("zemax_fft_mtf", unknown.RootElement);
        try { WorkerToolRegistry.BindArguments(method, cleaned.Arguments, default); throw new Exception("Unknown native option accepted."); }
        catch (ArgumentException) { }

        using var jobs = new McpJobManager(cancellationGrace: TimeSpan.FromMilliseconds(80));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var handle = BackgroundToolRunner.Start(jobs, "zemax_fft_mtf", 5, async token =>
        {
            Interlocked.Increment(ref executions); started.TrySetResult();
            await finish.Task.WaitAsync(token);
            return new { success = true, metric = 42 };
        });
        var id = JsonSerializer.SerializeToElement(handle).GetProperty("jobId").GetString()!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (jobs.Get(id)!.Progress != null || jobs.Get(id)!.State != McpJobState.Running)
            throw new Exception("Running native analysis must not fabricate intermediate numeric progress.");
        finish.TrySetResult();
        var completed = await WaitTerminalAsync(jobs, id);
        if (completed.State != McpJobState.Completed || executions != 1 ||
            JsonSerializer.SerializeToElement(completed.Result).GetProperty("metric").GetInt32() != 42)
            throw new Exception("Shared Job did not retain the actual result or executed more than once.");

        var failedHandle = BackgroundToolRunner.Start(jobs, "zemax_fft_mtf", 5,
            _ => Task.FromResult<object?>(new { success = false, error = "native-analysis-failed" }));
        var failedId = JsonSerializer.SerializeToElement(failedHandle).GetProperty("jobId").GetString()!;
        if ((await WaitTerminalAsync(jobs, failedId)).State != McpJobState.Failed)
            throw new Exception("Native success:false was incorrectly completed as success.");

        var timeoutHandle = BackgroundToolRunner.Start(jobs, "zemax_fft_mtf", 1, async token =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new { success = true }; });
        var timeoutId = JsonSerializer.SerializeToElement(timeoutHandle).GetProperty("jobId").GetString()!;
        var timedOut = await WaitTerminalAsync(jobs, timeoutId);
        if (timedOut.State != McpJobState.Cancelled || !timedOut.Message.Contains("Timed out"))
            throw new Exception("Shared Job timeout did not cancel and drain.");

        var lease = new OpticStudioControlLease();
        lease.ObserveGenerationStarted(12);
        using (await lease.AcquireAsync("reader", "zemax_fft_mtf", default, temporaryOwner: true))
        {
            lease.BindInvocation("reader", 12);
            if (!lease.RetainForJob("reader", "read-job", 12)) throw new Exception("Read-only Job lost its model lease.");
        }
        try { using var _ = await lease.AcquireObservationAsync("reader", default); throw new Exception("Owner entered STA while its background analysis was active."); }
        catch (ControlLeaseConflictException) { }
        try { using var _ = await lease.AcquireAsync("writer", "zemax_set_surface", default); throw new Exception("Writer entered during read-only background analysis."); }
        catch (ControlLeaseConflictException) { }
        lease.ObserveJob(12, new WorkerJobStatus { JobId = "read-job", State = "Completed" });
        using (await lease.AcquireAsync("writer", "zemax_set_surface", default)) { }
        Console.WriteLine("Expanded background execution: 23 capabilities, bounded controls, strict argument binding, honest progress, exactly-once results, failures, cancellation timeout and temporary read-only model ownership passed.");
    }

    private static async Task<McpJobSnapshot> WaitTerminalAsync(McpJobManager jobs, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            var value = jobs.Get(id)!;
            if (value.State is McpJobState.Completed or McpJobState.Cancelled or McpJobState.Failed) return value;
            await Task.Delay(10);
        }
        throw new TimeoutException("Background fixture did not reach terminal state.");
    }

    private static async Task VerifyExpandedBackgroundHttpAsync(HttpClient client, Uri endpoint)
    {
        async Task<JsonElement> Call(int id, string method, string? name, object? arguments, bool tasks, string? taskId = null)
        {
            var body = Build2026Body(id, method, method == "tools/call" ? name : null, "client-a", "instance-a");
            if (arguments != null) body = body.Replace("\"arguments\":{}", "\"arguments\":" + JsonSerializer.Serialize(arguments));
            if (tasks) body = body.Replace("\"io.modelcontextprotocol/clientCapabilities\":{}",
                "\"io.modelcontextprotocol/clientCapabilities\":{\"extensions\":{\"io.modelcontextprotocol/tasks\":{}}}");
            if (taskId != null) body = body.Replace("\"_meta\":{", "\"taskId\":" + JsonSerializer.Serialize(taskId) + ",\"_meta\":{");
            using var response = await client.SendAsync(Create2026Request(endpoint, body, method, name));
            var payload = await ReadFirstMcpPayloadAsync(response);
            using var parsed = JsonDocument.Parse(payload);
            if (!response.IsSuccessStatusCode || parsed.RootElement.TryGetProperty("error", out _))
                throw new Exception("Expanded background HTTP failed: " + payload);
            return parsed.RootElement.GetProperty("result").Clone();
        }
        static JsonElement Native(JsonElement result)
        {
            using var json = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
            return json.RootElement.Clone();
        }
        var synchronous = await Call(9301, "tools/call", "zemax_fft_mtf", new { frequency = 10 }, false);
        if (Native(synchronous).GetProperty("metric").GetInt32() != 42)
            throw new Exception("Ordinary clients lost the synchronous MTF result.");
        var legacyJob = await Call(9302, "tools/call", "zemax_fft_mtf", new { frequency = 10, runInBackground = true }, false);
        var jobId = Native(legacyJob).GetProperty("jobId").GetString()!;
        if (legacyJob.TryGetProperty("resultType", out var legacyType) && legacyType.GetString() == "task")
            throw new Exception("Non-Tasks client unexpectedly received a Task handle.");
        var cancelled = await Call(9303, "tools/call", "zemax_job_cancel", new { jobId }, false);
        if (Native(cancelled).GetProperty("state").GetString() != "Cancelled")
            throw new Exception("Shared-token Job cancellation was blocked by the optical lease.");
        var task = await Call(9304, "tools/call", "zemax_fft_mtf", new { frequency = 10 }, true);
        if (task.GetProperty("resultType").GetString() != "task")
            throw new Exception("Tasks-capable client did not automatically receive a Task for MTF.");
        var taskId = task.GetProperty("taskId").GetString()!;
        var first = await Call(9305, "tasks/get", taskId, null, true, taskId);
        if (first.GetProperty("status").GetString() != "working") throw new Exception("Fake analysis Task did not remain pollable.");
        using (var denied = await Send2026ToolCallAsync(client, endpoint, 9306, "zemax_get_system", "client-a", "instance-b"))
        {
            var payload = await ReadFirstMcpPayloadAsync(denied);
            if (!payload.Contains("\"isError\":true")) throw new Exception("Foreground STA operation entered during background analysis.");
        }
        var final = await Call(9307, "tasks/get", taskId, null, true, taskId);
        if (final.GetProperty("status").GetString() != "completed" || !final.GetRawText().Contains("42"))
            throw new Exception("Official Task did not retain the real Fake Worker result.");
        var explicitSync = await Call(9308, "tools/call", "zemax_fft_mtf", new { frequency = 10, runInBackground = false }, true);
        if ((explicitSync.TryGetProperty("resultType", out var syncType) && syncType.GetString() == "task") ||
            Native(explicitSync).GetProperty("metric").GetInt32() != 42)
            throw new Exception("Explicit synchronous selection was overridden by Tasks negotiation.");
        Console.WriteLine("Expanded background HTTP: ordinary sync/Job fallback, shared-token cancel, automatic Tasks, blocked STA interleaving, real result and explicit sync passed.");
    }

    public sealed class BackgroundBindingFixture
    {
        public object Execute(int field = 1, CancellationToken cancellationToken = default) => new { success = true, field };
    }
}
