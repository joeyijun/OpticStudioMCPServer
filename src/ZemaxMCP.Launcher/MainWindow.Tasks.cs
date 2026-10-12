using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

// UI coordination; shared state and lifecycle remain in MainWindow.xaml.cs.
public partial class MainWindow
{
    private readonly TaskCenterViewModel _taskCenter = new TaskCenterViewModel();

    private void RefreshTaskCenter(JArray? jobs, JObject? health = null)
    {
        _taskCenter.Update(jobs, health);
        RefreshTasksPage();
        TaskCenterSummary.Text = _taskCenter.Summary;
    }

    // Preserve existing reflection smoke tests while the presentation logic
    // moves out of the WPF window into a separately testable projection.
    private static List<BackgroundJobView> BuildTaskHistory(JArray? jobs, JObject? health) =>
        TaskPresentation.Build(jobs, health);

    private async Task RequestCancellationAsync(BackgroundJobView selection)
    {
        if (!LauncherDialog.Confirm(this, "Cancel optical task?",
            "Request cooperative cancellation of " + selection.DisplayText +
            "?\nOnly the authenticated owner may cancel. A running optical operation may take time to stop safely.",
            "Cancel task")) return;
        var endpoint = McpUrl;
        var token = McpToken;
        TasksPageCancel.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => selection.IsOfficialTask
                ? SendMcpJsonRpc(endpoint, token, "tasks/cancel",
                    new JObject { ["taskId"] = selection.TaskId }, selection.TaskId)
                    .ToString(Newtonsoft.Json.Formatting.None)
                : RequestJobCancellation(endpoint, token, selection.JobId));
            Report("Background Job cancel request: " + result);
        }
        catch (Exception ex)
        {
            Report("Background Job cancel denied or failed: " + ex.Message);
        }
        finally { await RefreshStatusAsync(); }
    }

    private void RefreshTasksPage()
    {
        if (TasksPageJobs == null) return;
        var selected = (TasksPageJobs.SelectedItem as BackgroundJobView)?.SelectionKey;
        var state = (TasksPageFilter?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "All";
        var search = TasksPageSearch?.Text ?? "";
        var visible = _taskCenter.UpdateVisible(state, search);
        TasksPageJobs.SelectedItem = TaskCenterViewModel.Select(visible, selected);
        TasksPageSummary.Text = _taskCenter.PageSummary;
        TasksPageEmpty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksPageEmpty.Text = _taskCenter.EmptyMessage(visible.Count);
    }

    private void TasksPageFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TasksPageJobs != null) RefreshTasksPage();
    }

    private void TasksPageSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TasksPageJobs != null) RefreshTasksPage();
    }

    private void TasksPageJobs_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TasksPageDetail == null || TasksPageCancel == null || TasksPageViewResult == null) return;
        var selected = TasksPageJobs?.SelectedItem as BackgroundJobView;
        TasksPageCancel.IsEnabled = selected?.IsActive == true;
        TasksPageViewResult.IsEnabled = selected != null;
        TasksPageTitle.Text = selected?.ToolName ?? "No task selected";
        TasksPageState.Text = selected?.State ?? "Idle";
        TasksPageMetadata.Text = TaskCenterViewModel.Metadata(selected);
        TasksPageProgress.IsIndeterminate = selected?.IsProgressIndeterminate == true;
        TasksPageProgress.Value = (selected?.ProgressFraction ?? 0) * 100;
        TasksPageProgressHint.Text = selected?.ProgressHint ?? "No numeric progress reported.";
        TasksPageMessage.Text = TaskCenterViewModel.Message(selected);
        TasksPageDetail.Text = TaskCenterViewModel.Detail(selected);
    }

    private async void TasksPageCancel_Click(object sender, RoutedEventArgs e)
    {
        if (TasksPageJobs.SelectedItem is BackgroundJobView job && job.IsActive)
            await RequestCancellationAsync(job);
    }

    private async void TasksPageViewResult_Click(object sender, RoutedEventArgs e)
    {
        if (TasksPageJobs.SelectedItem is not BackgroundJobView job) return;
        var endpoint = McpUrl;
        var token = McpToken;
        TasksPageViewResult.IsEnabled = false;
        TasksPageDetailsSection.IsExpanded = true;
        try
        {
            var text = await Task.Run(() => job.IsOfficialTask
                ? SendMcpJsonRpc(endpoint, token, "tasks/get",
                    new JObject { ["taskId"] = job.TaskId }, job.TaskId)
                    ["result"]?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "No Task result returned."
                : RequestJobTool(endpoint, token, job.JobId, "zemax_job_status"));
            TasksPageDetail.Text = text.Length <= 24000 ? text :
                text.Substring(0, 24000) + "\n[Display truncated to 24000 characters; full result stays on the Worker.]";
        }
        catch (Exception ex) { TasksPageDetail.Text = "Result retrieval unavailable: " + ex.Message; }
        finally { TasksPageViewResult.IsEnabled = true; }
    }

    private static string RequestJobCancellation(string endpoint, string token, string jobId) =>
        JobActionClient.RequestJobCancellation(endpoint, token, jobId);

    private static string RequestJobTool(string endpoint, string token, string jobId, string toolName) =>
        JobActionClient.RequestJobTool(endpoint, token, jobId, toolName);
}
