using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Server.Services.Jobs;

namespace ZemaxMCP.Server.Tools.Jobs;

[ZemaxToolType]
public sealed class McpJobTools
{
    private readonly McpJobManager _jobs;
    public McpJobTools(McpJobManager jobs) => _jobs = jobs;

    public record JobInfo(
        string JobId, string ToolName, string State, int QueuePosition,
        double? ProgressPercent, string Message, DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, double? ElapsedSeconds, object? Result);

    [ZemaxTool(Name = "zemax_job_status")]
    [Description("Get state, queue position, elapsed time, and progress for a background Zemax job.")]
    public JobInfo? Status([Description("Job identifier returned when the long-running tool was started")] string jobId) =>
        ToInfo(_jobs.Get(jobId));

    [ZemaxTool(Name = "zemax_job_list")]
    [Description("List recent background Zemax jobs, including queued, running, completed, cancelled, and failed jobs.")]
    public IReadOnlyList<JobInfo> List() => _jobs.List().Select(ToInfo).Where(x => x != null).Cast<JobInfo>().ToArray();

    [ZemaxTool(Name = "zemax_job_cancel")]
    [Description("Request cooperative cancellation of a queued or running Zemax job. A running ZOS-API call stops at its next safe cancellation point without restarting the MCP server.")]
    public JobInfo? Cancel([Description("Job identifier returned when the long-running tool was started")] string jobId)
    {
        _jobs.Cancel(jobId, out var snapshot);
        return ToInfo(snapshot);
    }

    internal static JobInfo? ToInfo(McpJobSnapshot? job) => job == null ? null : new JobInfo(
        job.JobId, job.ToolName, job.State.ToString(), job.QueuePosition,
        job.Progress is { } progress ? Math.Round(progress * 100, 1) : null,
        job.Message, job.QueuedAt, job.StartedAt, job.CompletedAt,
        job.Elapsed?.TotalSeconds is { } elapsed ? Math.Round(elapsed, 1) : null, job.Result);
}
