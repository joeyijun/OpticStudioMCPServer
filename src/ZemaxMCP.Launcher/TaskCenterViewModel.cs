using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

/// <summary>Stateful, UI-independent projection for the Task Center.
/// No WPF controls, network IO, credentials or mutable optical operations.</summary>
internal sealed class TaskCenterViewModel
{
    private List<BackgroundJobView> _history = new();
    internal int Count => _history.Count;
    internal int ActiveCount => _history.Count(item=>item.IsActive);
    internal string Summary => Count==0 ? "No background tasks reported." :
        ActiveCount+" active · "+(Count-ActiveCount)+" recent";
    internal string PageSummary => Count==0 ? "Background activity from your AI clients." : Summary;

    internal void Update(JArray? jobs,JObject? health)
    {
        _history=TaskPresentation.Build(jobs,health);
    }

    internal List<BackgroundJobView> Visible(string? filter,string? search)
    {
        var state=string.IsNullOrWhiteSpace(filter) ? "All" : filter;
        var query=search?.Trim() ?? "";
        return _history.Where(item=>
            (state=="All" ||
             (state=="Running" && (item.State.Equals("Working",StringComparison.OrdinalIgnoreCase) ||
                                   item.State.Equals("Cancelling",StringComparison.OrdinalIgnoreCase))) ||
             string.Equals(item.State,state,StringComparison.OrdinalIgnoreCase)) &&
            (query.Length==0 ||
             item.ToolName.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 ||
             item.JobId.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 ||
             item.TaskId.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 ||
             item.State.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0))
            .OrderByDescending(item=>item.IsActive).ToList();
    }

    internal static BackgroundJobView? Select(IReadOnlyList<BackgroundJobView> visible,
        string? selectionKey) => visible.FirstOrDefault(item=>item.SelectionKey==selectionKey) ??
            visible.FirstOrDefault(item=>item.IsActive) ?? visible.FirstOrDefault();

    internal string EmptyMessage(int filteredCount) => Count==0 ?
        "No recent tasks. Start a background operation from your AI client." :
        filteredCount==0 ? "No tasks match these filters or search terms." : "";

    internal static string Metadata(BackgroundJobView? selected) => selected==null ?
        "Tasks appear here when an AI starts a background operation." :
        (selected.IsOfficialTask ? "Task "+selected.TaskId : "Job "+selected.JobId) +
        " · "+selected.Owner+" · elapsed "+selected.Elapsed;

    internal static string Message(BackgroundJobView? selected) => selected==null ? "" :
        selected.Message+(selected.RecommendedAction.Length==0 ? "" :
            "\nSuggested action: "+selected.RecommendedAction);

    internal static string Detail(BackgroundJobView? selected)
    {
        if(selected==null) return "Select a Job to inspect its details.";
        return (selected.IsOfficialTask ? "Task ID: "+selected.TaskId+"\nLinked Job: "+selected.JobId :
            "Job ID: "+selected.JobId)+"\nTool: "+selected.ToolName+
            "\nState: "+selected.State+
            "\nWorker generation: "+selected.WorkerGeneration+
            "\nOwner visibility: "+selected.Owner+
            "\nElapsed: "+selected.Elapsed+
            "\nProgress: "+(string.IsNullOrWhiteSpace(selected.Progress) ? "Not reported" : selected.Progress)+
            "\nQueue: "+(string.IsNullOrWhiteSpace(selected.Queue) ? "Not queued" : selected.Queue)+
            "\nWorker message / failure reason: "+selected.Message+
            (selected.RecommendedAction.Length==0 ? "" : "\nSuggested action: "+selected.RecommendedAction)+
            "\n\n'View result' calls Tasks/get for owned official Tasks or job_status for Worker Jobs. "+
            "A completed Job may have expired its result; official Tasks require their separate Task ID.";
    }
}
