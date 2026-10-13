using System.Text.Json;

namespace ZemaxMCP.Server.Services.Jobs;

internal static class BackgroundToolRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static object Start(McpJobManager jobs, string tool, int timeoutSeconds,
        Func<CancellationToken, Task<object?>> invoke)
    {
        if (!ZemaxMCP.ToolManifest.BackgroundToolPolicy.UsesSharedRunner(tool))
            throw new ArgumentException("This tool has no shared background execution capability.", nameof(tool));
        if (timeoutSeconds < 1 || timeoutSeconds > 7200)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        var job = jobs.Enqueue(tool, async context =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportMessage("Running; no reliable intermediate numeric progress is available.");
            var result = await invoke(context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            if (result == null) throw new InvalidOperationException("The background tool returned no result.");
            var json = JsonSerializer.SerializeToElement(result, JsonOptions);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                context.SetResult(result);
                var message = json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString() : "The background optical operation failed.";
                throw new InvalidOperationException(message);
            }
            context.SetResult(result);
            context.ReportProgress(1, "Completed.");
        }, TimeSpan.FromSeconds(timeoutSeconds));
        return new { success = true, jobId = job.JobId, state = job.State.ToString(),
            toolName = tool, background = true, timeoutSeconds,
            guidance = "Operation accepted, not yet optically verified. Poll the Job/Task to its terminal state and inspect the actual result." };
    }
}
