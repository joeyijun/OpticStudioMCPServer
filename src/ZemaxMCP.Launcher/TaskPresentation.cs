using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

// Task view-model/projection is UI-independent. It deliberately never
// performs an MCP request, takes a control lease or reveals foreign Jobs.
internal sealed class BackgroundJobView
    {
        public string JobId { get; set; } = "";
        public string TaskId { get; set; } = "";
        public bool IsOfficialTask => TaskId.Length > 0;
        public string State { get; set; } = "";
        public string DisplayText { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string Message { get; set; } = "";
        public string Owner { get; set; } = "Not individually reported";
        public string WorkerGeneration { get; set; } = "Not reported";
        public string Elapsed { get; set; } = "Not reported";
        public string Queue { get; set; } = "";
        public string Progress { get; set; } = "";
        public double? ProgressFraction { get; set; }
        public bool IsProgressIndeterminate => IsActive &&
            (!ProgressFraction.HasValue || ProgressFraction.Value <= 0 || ProgressFraction.Value >= 1);
        public string ProgressHint => IsProgressIndeterminate
            ? (State.Equals("Queued", StringComparison.OrdinalIgnoreCase) ? "Queued" : "In progress") +
                " · elapsed " + Elapsed + " · no intermediate estimate reported"
            : ProgressFraction.HasValue ? Math.Round(ProgressFraction.Value * 100) + "% reported · elapsed " + Elapsed
            : "No numeric progress reported.";
        public string SelectionKey => IsOfficialTask ? "task:" + TaskId : "job:" + JobId;
        public string ActivitySubtitle => (IsOfficialTask ? "MCP Task" : "Worker Job") + " · " + Elapsed +
            (string.IsNullOrWhiteSpace(Progress) ? "" : Progress);
        public bool IsActive => State.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
            State.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
            State.Equals("Working", StringComparison.OrdinalIgnoreCase) ||
            State.Equals("Cancelling", StringComparison.OrdinalIgnoreCase);
    }


internal static class TaskPresentation
{
    internal static List<BackgroundJobView> Build(JArray? jobs, JObject? health)
    {
        var items = (jobs ?? new JArray()).OfType<JObject>()
            .Where(job => !string.IsNullOrWhiteSpace(job["jobId"]?.ToString()))
            .Take(25)
            .Select(job =>
            {
                var id = job["jobId"]!.ToString();
                var state = job["state"]?.ToString() ?? "Unknown";
                var tool = job["toolName"]?.ToString() ?? job["tool"]?.ToString() ?? "ZOS-API Job";
                var progress = job["fraction"]?.Value<double?>() ?? job["progress"]?.Value<double?>();
                var active = new BackgroundJobView { State = state }.IsActive;
                var pct = progress.HasValue && !double.IsNaN(progress.Value) &&
                    !double.IsInfinity(progress.Value) && progress.Value >= 0 && progress.Value <= 1 &&
                    (!active || (progress.Value > 0 && progress.Value < 1))
                    ? " · " + Math.Round(progress.Value * 100) + "%" : "";
                var queue = job["queuePosition"]?.Value<int?>() is { } position && position > 0
                    ? " · queue " + position : "";
                return new BackgroundJobView
                {
                    JobId = id,
                    ToolName = tool,
                    Message = job["message"]?.ToString() ?? "No Worker message.",
                    Owner = job["owner"]?.ToString() ??
                        (health?["clientIsolation"]?.ToString().Contains("scoped") == true ? "Authenticated credential (owner-filtered)" :
                         (health?["controlLease"]?["owner"] == null ? "Not individually reported" :
                            "current control lease: " + health["controlLease"]?["owner"]?.ToString() +
                            " (may differ from Job creator)")),
                    WorkerGeneration = health?["worker"]?["workerGeneration"]?.ToString() ?? "Not reported",
                    Elapsed = job["elapsedSeconds"]?.Value<double?>() is double seconds &&
                        !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0
                        ? seconds.ToString("F0") + "s" : job["elapsed"]?.ToString() ?? "Not reported",
                    Queue = queue,
                    Progress = pct,
                    ProgressFraction = progress.HasValue && !double.IsNaN(progress.Value) &&
                        !double.IsInfinity(progress.Value) && progress.Value >= 0 && progress.Value <= 1
                        ? progress : null,
                    State = state,
                    DisplayText = tool + " · " + state + pct + queue + " · " + id.Substring(0, Math.Min(id.Length, 8))
                };
            }).ToList();

        if (health?["tasks"] is JArray ownerTasks)
        {
            foreach (var record in ownerTasks.OfType<JObject>().Take(25))
            {
                var taskId = record["taskId"]?.ToString() ?? "";
                if (taskId.Length == 0) continue;
                var linkedJobId = record["jobId"]?.ToString() ?? "";
                var generation = record["generation"]?.ToString() ?? "";
                // The official Task wraps this same Worker Job: showing both
                // as separate items doubles the apparent task count. Merge
                // only when the Job belongs to the SAME Worker generation;
                // restarted-journal Tasks must never absorb a new Job ID.
                var linkedJob = linkedJobId.Length == 0 ? null : items.FirstOrDefault(
                    item => !item.IsOfficialTask && item.JobId == linkedJobId &&
                        generation.Length > 0 && item.WorkerGeneration == generation);
                if (linkedJob != null) items.Remove(linkedJob);
                var state = record["state"]?.ToString() ?? "working";
                var taskActive = state.Equals("working", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("queued", StringComparison.OrdinalIgnoreCase) ||
                    state.Equals("cancelling", StringComparison.OrdinalIgnoreCase);
                var started = DateTimeOffset.TryParse(record["createdAt"]?.ToString(), out var born)
                    ? born : DateTimeOffset.UtcNow;
                var completedAt = DateTimeOffset.TryParse(record["updatedAt"]?.ToString(), out var updated)
                    ? updated : DateTimeOffset.UtcNow;
                var elapsed = ((state == "working" ? DateTimeOffset.UtcNow : completedAt) - started).TotalSeconds;
                items.Add(new BackgroundJobView
                {
                    TaskId = taskId,
                    JobId = linkedJobId,
                    State = state,
                    ToolName = linkedJob?.ToolName ?? "Official MCP Task",
                    Owner = "This authenticated credential",
                    WorkerGeneration = generation.Length > 0 ? generation : "Not reported",
                    Elapsed = linkedJob?.Elapsed ?? Math.Max(0, elapsed).ToString("F0") + "s",
                    Queue = linkedJob?.Queue ?? "",
                    // A terminal Task must not inherit a stale 42% reading
                    // from its still-cached Worker Job.
                    Progress = taskActive ? linkedJob?.Progress ?? "" : "",
                    ProgressFraction = taskActive ? linkedJob?.ProgressFraction : null,
                    Message = (record["message"]?.ToString() ?? "") +
                        (record["cancelRequested"]?.Value<bool>() == true ? " · cancellation requested" : "") +
                        (record["resultExpired"]?.Value<bool>() == true ? " · result expired" : "") +
                        (linkedJob == null ? "" : " · " + linkedJob.Message),
                    DisplayText = "MCP Task · " + state + " · " + taskId.Substring(0, Math.Min(8, taskId.Length))
                });
            }
        }
        return items;
    }
}
