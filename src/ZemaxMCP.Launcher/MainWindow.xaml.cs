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

public partial class MainWindow : Window
{
    private Process? _bridge;
    private int _bridgeRestartAttempts;
    private bool _exitRequested;
    private bool _clientSetupPrompted;
    private bool _refreshingStatus;
    private bool _refreshingActivity;
    private bool _healthReachable;
    private bool _activityEndpointSupported = true;
    private DateTimeOffset _activityEndpointRetryAfter;
    private string _observedActivityEndpoint = "";
    private bool _windowLoaded;
    private bool _settingsLoadFailed;
    private string _localAccessToken = "";
    private string _remoteEndpoint = "";
    private string _remoteAccessToken = "";
    private string _fullDiagnostics = "Status has not been checked yet.";
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _activityTimer;
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyMaterial();
        SystemEvents.UserPreferenceChanged += AppearancePreferenceChanged;
        SystemEvents.SessionSwitch += AppearanceSessionChanged;
        var applicationIcon = GetApplicationIcon();
        _trayIcon = new Forms.NotifyIcon { Icon = applicationIcon, Text = "Zemax MCP", Visible = true };
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(RestoreWindow));
        };
        var menu = new RoundedTrayMenu
        {
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = false,
            BackColor = Forms.SystemInformation.HighContrast ? System.Drawing.SystemColors.Menu : System.Drawing.Color.FromArgb(248, 250, 253),
            ForeColor = Forms.SystemInformation.HighContrast ? System.Drawing.SystemColors.MenuText : System.Drawing.Color.FromArgb(29, 29, 31),
            Font = new System.Drawing.Font("Segoe UI", 10),
            Padding = new Forms.Padding(5)
        };
        menu.Items.Add("Start", null, (_, _) => Dispatcher.BeginInvoke(new Action(StartServiceFromTray)));
        menu.Items.Add("Stop", null, (_, _) => Dispatcher.BeginInvoke(new Action(StopService)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication)));
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            if (item is Forms.ToolStripMenuItem)
                item.Padding = new Forms.Padding(14, 8, 24, 8);
        }
        _trayIcon.ContextMenuStrip = menu;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _activityTimer.Tick += async (_, _) => await RefreshActivityAsync();
    }

    private static System.Drawing.Icon GetApplicationIcon()
    {
        try
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrWhiteSpace(executable)
                ? System.Drawing.SystemIcons.Application
                : System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? System.Drawing.SystemIcons.Application;
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        var installs = ZemaxInstallation.FindAll();
        var savedRoot = ReadSetting("zemaxRoot");
        if (!string.IsNullOrWhiteSpace(savedRoot) && installs.All(x => !x.Root.Equals(savedRoot, StringComparison.OrdinalIgnoreCase)))
        {
            var savedManual = ZemaxInstallation.FromFolder(savedRoot!);
            if (savedManual != null) installs.Add(savedManual);
        }
        ZemaxVersions.ItemsSource = installs;
        ZemaxVersions.SelectedItem = installs.FirstOrDefault(x => x.Root.Equals(savedRoot, StringComparison.OrdinalIgnoreCase));
        if (ZemaxVersions.SelectedItem == null) ZemaxVersions.SelectedIndex = installs.Count > 0 ? 0 : -1;
        var hasRemoteEndpoint = IsRemoteEndpointConfigured;
        Report(_settingsLoadFailed
            ? "Saved preferences could not be read. The file is preserved; restore launcher-settings.json from a valid backup. Automatic service startup is skipped."
            : hasRemoteEndpoint
            ? "Using the saved remote MCP endpoint. Local service startup is skipped."
            : installs.Count == 0
            ? "No local OpticStudio found. Paste secure setup from the OpticStudio computer, then select Test MCP and Configure clients."
            : "Starting local MCP endpoint automatically…");
        RefreshEndpoint();
        _windowLoaded = true;
        ApplyMaterial();
        if (installs.Count > 0 && !hasRemoteEndpoint && !_settingsLoadFailed) StartBridge();
        SetIndicatorsChecking();
        _statusTimer.Start();
        _activityTimer.Start();
        RefreshClientDashboard(null);
        OfferFirstRunClientSetup();
    }
    private string SelectedMaterial => (MaterialChoice.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "mica";

    private void ApplyMaterial()
    {
        MaterialChoice.ToolTip = WindowMaterial.Apply(this, SelectedMaterial) +
            "\nMica: subtle wallpaper tint. Acrylic: frosted desktop blur. Solid: no transparency.";
    }

    private void MaterialChoice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        ApplyMaterial();
        SaveSettings();
    }

    private void AppearancePreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueMaterialRefresh();
    private void AppearanceSessionChanged(object sender, SessionSwitchEventArgs e) => QueueMaterialRefresh();
    private void QueueMaterialRefresh()
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => { if (_windowLoaded && !_exitRequested) ApplyMaterial(); }));
    }

    private void ZemaxVersions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        RefreshEndpoint();
        SaveSettings();
        if (IsRemoteEndpointConfigured) return;
        StopBridge();
        if (Installation != null) StartBridge();
    }
    private void ChooseZemaxFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select the OpticStudio installation folder containing ZOSAPI.dll",
            SelectedPath = Installation?.Root ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var manual = ZemaxInstallation.FromFolder(dialog.SelectedPath);
        if (manual == null)
        {
            Report("That folder is not a usable OpticStudio installation. It must contain ZOSAPI.dll, ZOSAPI_Interfaces.dll, and ZOSAPI_NetHelper.dll (the NetHelper may also be under ZOS-API\\Libraries). No setting was changed.");
            return;
        }
        var installs = (ZemaxVersions.ItemsSource as IEnumerable<ZemaxInstallation> ?? Array.Empty<ZemaxInstallation>()).ToList();
        var selected = installs.FirstOrDefault(x => x.Root.Equals(manual.Root, StringComparison.OrdinalIgnoreCase));
        if (selected == null)
        {
            installs.Add(manual);
            installs = installs.OrderByDescending(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            selected = manual;
            ZemaxVersions.ItemsSource = installs;
        }
        ZemaxVersions.SelectedItem = selected;
        Report("Using manually selected OpticStudio folder: " + selected.Root);
    }
    private void Port_LostFocus(object sender, RoutedEventArgs e) { RefreshEndpoint(); SaveSettings(); }
    private async void RemoteSecureSetup_LostFocus(object sender, RoutedEventArgs e)
    {
        var setup = RemoteSecureSetup.Text;
        if (string.IsNullOrWhiteSpace(setup)) return;
        if (!TryApplySecureSetup(setup))
        {
            Report("Paste the complete secure setup copied from the OpticStudio computer. It includes both the MCP address and access token.");
            return;
        }
        SaveSettings();
        StopBridge();
        await RefreshStatusAsync();
    }
    private void ClearRemoteSetup_Click(object sender, RoutedEventArgs e)
    {
        _remoteEndpoint = "";
        _remoteAccessToken = "";
        RemoteSecureSetup.Text = "";
        UpdateRemoteSetupStatus();
        SaveSettings();
        Report("Remote secure setup cleared. This computer will use its local MCP service.");
        if (Installation != null && (_bridge == null || _bridge.HasExited)) StartBridge();
    }
    private string HostName => ShareOnLan.IsChecked == true ? "0.0.0.0" : "127.0.0.1";
    private void RefreshEndpoint() => Endpoint.Text = Url;
    private ZemaxInstallation? Installation => ZemaxVersions.SelectedItem as ZemaxInstallation;
    private string Url => "http://" + (ShareOnLan.IsChecked == true ? GetLanAddress() : "127.0.0.1") + ":" + Port.Text + "/mcp";
    private bool IsRemoteEndpointConfigured => Uri.TryCreate(_remoteEndpoint, UriKind.Absolute, out var remote) &&
        (remote.Scheme == Uri.UriSchemeHttp || remote.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrWhiteSpace(_remoteAccessToken);
    private string McpUrl => Uri.TryCreate(_remoteEndpoint, UriKind.Absolute, out var remote) &&
        (remote.Scheme == Uri.UriSchemeHttp || remote.Scheme == Uri.UriSchemeHttps) ? remote.ToString().TrimEnd('/') : Url;
    private string McpToken => IsRemoteEndpointConfigured ? _remoteAccessToken : _localAccessToken;
    private string SelectedToolsetProfile => (ToolsetProfile.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "full-expert";

    private void ShareOnLan_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowLoaded) return;
        RefreshEndpoint();
        SaveSettings();
        if (IsRemoteEndpointConfigured) return;
        StopBridge();
        if (Installation != null) StartBridge();
    }
    private void ReadOnlyMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowLoaded) return;
        SaveSettings();
        if (IsRemoteEndpointConfigured) return;
        StopBridge();
        if (Installation != null) StartBridge();
    }
    private void ToolsetProfile_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        SaveSettings();
        if (IsRemoteEndpointConfigured) return;
        StopBridge();
        if (Installation != null) StartBridge();
        Report("Run configuration changed to " + (ToolsetProfile.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content + ".");
    }
    private void OfficialTasks_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowLoaded) return;
        SaveSettings();
        // Never terminate a running optical operation merely to change a preference.
        Report(IsRemoteEndpointConfigured
            ? "Change official Tasks on the OpticStudio computer; this setting does not change the remote service."
            : "Official Tasks preference saved. Apply it with Stop / Start when no optical operation is running.");
    }
    private void StartOnLogin_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowLoaded) return;
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartOnLogin.IsChecked == true) run?.SetValue("ZemaxMCP", "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Start-Zemax-MCP.exe") + "\"");
            else run?.DeleteValue("ZemaxMCP", false);
            SaveSettings();
        }
        catch (Exception ex) { Report("Could not change sign-in startup: " + ex.Message); }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartServiceFromTray();
    private void StartServiceFromTray()
    {
        if (IsRemoteEndpointConfigured)
        {
            Report("This computer is configured as an AI client for a remote MCP service.");
            return;
        }
        if (_bridge != null && !_bridge.HasExited)
        {
            Report("HTTP MCP is already running.");
            return;
        }
        StartBridge(false);
    }
    private void StartBridge(bool automaticRestart = false)
    {
        if (Installation == null) { Report("Choose a detected OpticStudio installation first."); return; }
        if (!int.TryParse(Port.Text, out var port) || port < 1 || port > 65535)
        {
            Report("Port must be a number from 1 to 65535.");
            return;
        }
        StopBridge();
        if (!automaticRestart) _bridgeRestartAttempts = 0;
        SaveSettings();
        // Host is self-contained .NET 10 so it must keep its runtime files in
        // a private directory rather than collide with net48 desktop binaries.
        var bridge = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Host", "ZemaxMCP.Host.exe");
        if (!File.Exists(bridge)) bridge = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZemaxMCP.Host.exe"); // legacy package fallback
        var server = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZemaxMCP.Worker.exe");
        if (!File.Exists(bridge) || !File.Exists(server)) { Report("Release package is incomplete: the Host folder and ZemaxMCP.Worker.exe are required."); return; }
        if (!EnsureZosApiBootstrap(Installation)) return;
        // URL ACL/firewall setup is a user-approved configuration step, not
        // something an automatic recovery attempt should prompt for again.
        var firewallReady = automaticRestart || ShareOnLan.IsChecked != true || FirewallRule.TryEnsure(port);
        Process? process;
        try
        {
            var snapshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "snapshots");
            var networkAllowlist = ShareOnLan.IsChecked == true
                ? $" --allowed-host {GetLanAddress()} --allowed-origin http://{GetLanAddress()}:*"
                : string.Empty;
            var startInfo = new ProcessStartInfo(bridge,
                $"--server \"{server}\" --zemax-root \"{Installation.Root}\" --host {HostName} --port {port} --read-only {(ReadOnlyMode.IsChecked == true ? "true" : "false")} --toolset {SelectedToolsetProfile} --snapshot-dir \"{snapshots}\" " + OfficialTasksSettings.HostArgument(OfficialTasks.IsChecked == true) + networkAllowlist)
            { UseShellExecute = false, CreateNoWindow = true };
            startInfo.EnvironmentVariables["ZEMAX_MCP_TOKEN"] = _localAccessToken;
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Report("Could not start the MCP bridge: " + ex.Message);
            return;
        }
        if (process == null) { Report("Windows did not create the MCP bridge process."); return; }
        _bridge = process;
        process.EnableRaisingEvents = true;
        process.Exited += async (_, _) => await Dispatcher.InvokeAsync(() => HandleBridgeExitAsync(process));
        Report("HTTP MCP started: " + Url + Environment.NewLine + "Logs: " + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs") +
            (firewallReady ? "" : Environment.NewLine + "Firewall permission was not granted; another PC may not reach this endpoint."));
        ScheduleStatusRefresh();
    }
    private async Task HandleBridgeExitAsync(Process exitedProcess)
    {
        if (!ReferenceEquals(_bridge, exitedProcess) || _exitRequested || IsRemoteEndpointConfigured) return;
        _bridge = null;
        if (_bridgeRestartAttempts >= 3)
        {
            Report("MCP bridge stopped repeatedly. Automatic recovery is paused; open Logs, then use Start service after correcting the cause.");
            return;
        }
        _bridgeRestartAttempts++;
        var delay = Math.Min(8, 1 << _bridgeRestartAttempts);
        Report("MCP bridge stopped unexpectedly. Automatic recovery attempt " + _bridgeRestartAttempts + "/3 starts in " + delay + " seconds…");
        await Task.Delay(TimeSpan.FromSeconds(delay));
        if (_bridge == null && !_exitRequested && !IsRemoteEndpointConfigured) StartBridge(true);
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopService();
    private void StopService()
    {
        StopBridge();
        Report("HTTP MCP stopped.");
        SetIndicator(McpStateDot, McpState, "Stopped", System.Windows.Media.Brushes.IndianRed);
        SetIndicator(AiStateDot, AiState, "No client activity while stopped", System.Windows.Media.Brushes.SlateGray);
    }
    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
    private async Task RefreshActivityAsync()
    {
        if (_refreshingActivity || !_healthReachable) return;
        var endpoint = McpUrl;
        if (!string.Equals(endpoint, _observedActivityEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            _observedActivityEndpoint = endpoint;
            _activityEndpointSupported = true;
        }
        if (!_activityEndpointSupported && DateTimeOffset.UtcNow < _activityEndpointRetryAfter) return;
        _refreshingActivity = true;
        var accessToken = McpToken;
        try
        {
            var activity = await Task.Run(() => GetEndpointJson(endpoint, accessToken, "/activity", 2000));
            _activityEndpointSupported = true;
            if (_healthReachable && string.Equals(endpoint, McpUrl, StringComparison.OrdinalIgnoreCase))
                RefreshClientDashboard(activity, refreshSetup: false);
        }
        catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
        {
            // Older remote Hosts still provide activity through /health.
            _activityEndpointSupported = false;
            _activityEndpointRetryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        catch (Exception) { /* The next full health check owns offline state. */ }
        finally { _refreshingActivity = false; }
    }
    private sealed class BackgroundJobView
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
        public bool IsActive => State.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
            State.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
            State.Equals("Working", StringComparison.OrdinalIgnoreCase) ||
            State.Equals("Cancelling", StringComparison.OrdinalIgnoreCase);
    }

    private List<BackgroundJobView> _taskHistory = new List<BackgroundJobView>();

    private void RefreshTaskCenter(JArray? jobs, JObject? health = null)
    {
        var selectedId = (TaskCenterJobs.SelectedItem as BackgroundJobView)?.JobId;
        var items = (jobs ?? new JArray()).OfType<JObject>()
            .Where(job => !string.IsNullOrWhiteSpace(job["jobId"]?.ToString()))
            .Take(25)
            .Select(job =>
            {
                var id = job["jobId"]!.ToString();
                var state = job["state"]?.ToString() ?? "Unknown";
                var tool = job["toolName"]?.ToString() ?? job["tool"]?.ToString() ?? "ZOS-API Job";
                var progress = job["fraction"]?.Value<double?>() ?? job["progress"]?.Value<double?>();
                var pct = progress.HasValue && !double.IsNaN(progress.Value) &&
                    !double.IsInfinity(progress.Value) && progress.Value >= 0 && progress.Value <= 1
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
                    Elapsed = job["elapsedSeconds"]?.ToString() ?? job["elapsed"]?.ToString() ?? "Not reported",
                    Queue = queue,
                    Progress = pct,
                    State = state,
                    DisplayText = tool + " · " + state + pct + queue + " · " + id.Substring(0, Math.Min(id.Length, 8))
                };
            }).ToList();

        _taskHistory = items;
        if (health?["tasks"] is JArray ownerTasks)
        {
            foreach (var record in ownerTasks.OfType<JObject>().Take(25))
            {
                var taskId = record["taskId"]?.ToString() ?? "";
                if (taskId.Length == 0) continue;
                var linkedJobId = record["jobId"]?.ToString() ?? "";
                var state = record["state"]?.ToString() ?? "working";
                var started = DateTimeOffset.TryParse(record["createdAt"]?.ToString(), out var born)
                    ? born : DateTimeOffset.UtcNow;
                var completedAt = DateTimeOffset.TryParse(record["updatedAt"]?.ToString(), out var updated)
                    ? updated : DateTimeOffset.UtcNow;
                var elapsed = ((state == "working" ? DateTimeOffset.UtcNow : completedAt) - started).TotalSeconds;
                _taskHistory.Add(new BackgroundJobView
                {
                    TaskId = taskId,
                    JobId = linkedJobId,
                    State = state,
                    ToolName = "Official MCP Task",
                    Owner = "This authenticated credential",
                    WorkerGeneration = record["generation"]?.ToString() ?? "Not reported",
                    Elapsed = Math.Max(0, elapsed).ToString("F0") + "s",
                    Message = (record["message"]?.ToString() ?? "") +
                        (record["cancelRequested"]?.Value<bool>() == true ? " · cancellation requested" : "") +
                        (record["resultExpired"]?.Value<bool>() == true ? " · result expired" : ""),
                    DisplayText = "MCP Task · " + state + " · " + taskId.Substring(0, Math.Min(8, taskId.Length))
                });
            }
        }
        RefreshTasksPage();
        TaskCenterJobs.ItemsSource = items;
        TaskCenterJobs.SelectedItem = items.FirstOrDefault(item => item.JobId == selectedId)
            ?? items.FirstOrDefault(item => item.IsActive) ?? items.FirstOrDefault();
        var active = items.Count(item => item.IsActive);
        TaskCenterSummary.Text = items.Count == 0
            ? "No background Jobs reported for this client."
            : active + " active · " + (items.Count - active) + " recent · most recent 25";
        TaskCenterCancel.IsEnabled = (TaskCenterJobs.SelectedItem as BackgroundJobView)?.IsActive == true;
    }

    private void TaskCenterJobs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        TaskCenterCancel.IsEnabled = (TaskCenterJobs.SelectedItem as BackgroundJobView)?.IsActive == true;
    }

    private async void TaskCenterCancel_Click(object sender, RoutedEventArgs e)
    {
        if (TaskCenterJobs.SelectedItem is not BackgroundJobView selection || !selection.IsActive) return;
        await RequestCancellationAsync(selection);
    }

    private async Task RequestCancellationAsync(BackgroundJobView selection)
    {
        var decision = System.Windows.MessageBox.Show(
            "Request cooperative cancellation of " + selection.DisplayText +
            "?\nOnly the authenticated Job owner may cancel; other clients will be denied.",
            "Cancel optical Job", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (decision != MessageBoxResult.Yes) return;
        var endpoint = McpUrl;
        var token = McpToken;
        TaskCenterCancel.IsEnabled = false;
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
        var selected = (TasksPageJobs.SelectedItem as BackgroundJobView)?.JobId;
        var state = (TasksPageFilter?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "All";
        var visible = _taskHistory.Where(item => state == "All" ||
            (state == "Running" && item.State.Equals("Working", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(item.State, state, StringComparison.OrdinalIgnoreCase)).ToList();
        TasksPageJobs.ItemsSource = visible;
        TasksPageJobs.SelectedItem = visible.FirstOrDefault(item => item.JobId == selected) ??
            visible.FirstOrDefault();
        TasksPageSummary.Text = visible.Count + " shown · " + _taskHistory.Count +
            " reported (max 25 Worker Jobs plus 25 owner-visible Tasks).";
    }

    private void TasksPageFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TasksPageJobs != null) RefreshTasksPage();
    }

    private void TasksPageJobs_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TasksPageDetail == null || TasksPageCancel == null || TasksPageViewResult == null) return;
        var selected = TasksPageJobs?.SelectedItem as BackgroundJobView;
        TasksPageCancel.IsEnabled = selected?.IsActive == true;
        TasksPageViewResult.IsEnabled = selected != null;
        TasksPageDetail.Text = selected == null ? "Select a Job to inspect its details." :
            (selected.IsOfficialTask ? "Task ID: " + selected.TaskId + "\nLinked Job: " + selected.JobId :
                "Job ID: " + selected.JobId) + "\nTool: " + selected.ToolName +
            "\nState: " + selected.State +
            "\nWorker generation: " + selected.WorkerGeneration +
            "\nOwner visibility: " + selected.Owner +
            "\nElapsed: " + selected.Elapsed +
            "\nProgress: " + (string.IsNullOrWhiteSpace(selected.Progress) ? "Not reported" : selected.Progress) +
            "\nQueue: " + (string.IsNullOrWhiteSpace(selected.Queue) ? "Not queued" : selected.Queue) +
            "\nWorker message / failure reason: " + selected.Message +
            "\n\n'View result' calls Tasks/get for owned official Tasks or job_status for Worker Jobs. "+
            "A completed Job may have expired its result; official Tasks require their separate Task ID.";
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

    private static string RequestJobCancellation(string endpoint, string accessToken, string jobId)
    {
        _ = RequestJobTool(endpoint, accessToken, jobId, "zemax_job_cancel");
        return "accepted for " + jobId + "; final state will be confirmed by the next status poll.";
    }

    private static string RequestJobTool(string endpoint, string accessToken, string jobId, string toolName)
    {
        // Always route through ordinary MCP tools/call so scoped ownership and
        // the Worker's generation check remain authoritative. This is not an
        // elevated Launcher-specific administrative cancellation API.
        var body = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JObject
            {
                ["name"] = toolName,
                ["arguments"] = new JObject { ["jobId"] = jobId },
                ["_meta"] = new JObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientInfo"] = new JObject
                    {
                        ["name"] = "zemax-launcher",
                        ["version"] = "1.5.0"
                    }
                }
            }
        };
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "application/json, text/event-stream";
        request.Timeout = 15000;
        request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        request.Headers["Mcp-Method"] = "tools/call";
        request.Headers["Mcp-Name"] = toolName;
        AddAuthorization(request, accessToken);
        var bytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream());
        var raw = reader.ReadToEnd();
        if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            raw = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))?.Substring(5) ?? "";
        }
        var rpc = JObject.Parse(raw);
        if (rpc["error"] != null || rpc["result"]?["isError"]?.Value<bool>() == true)
            throw new InvalidOperationException("MCP rejected the request (not owner, stale generation, or cancellation error).");
        return rpc["result"]?["content"]?.FirstOrDefault()?["text"]?.ToString() ??
            rpc["result"]?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "No Job result payload.";
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshingStatus) return;
        _refreshingStatus = true;
        var root = Installation?.Root;
        var endpoint = McpUrl;
        // Capture UI-owned values before switching to the worker thread.  In
        // remote mode McpToken reads the PasswordBox, which must only ever be
        // accessed on this WPF dispatcher thread.
        var accessToken = McpToken;
        var apiFiles = Installation?.ApiFilesPresent == true;
        var localBridge = _bridge != null && !_bridge.HasExited;
        try
        {
            var health = await Task.Run(() => GetHealth(endpoint, accessToken));
            _healthReachable = true;
            var apiLoaded = health["zosApiLoaded"]?.Value<bool>() == true;
            var apiConnected = health["zosApiConnected"]?.Value<bool>() == true;
            var licenseStatus = health["licenseStatus"]?.ToString() ?? "Not checked";
            var bridgeRunning = health["bridgeRunning"]?.Value<bool>() == true;
            var serverRunning = health["mcpServerRunning"]?.Value<bool>() == true;
            var activeRequests = health["activeRequests"]?.Value<int>() ?? 0;
            var activeOperations = health["activeOperations"] as JArray;
            var activeOperation = activeOperations?.FirstOrDefault();
            var activeOperationText = activeOperation == null ? "" :
                "; current: " + (activeOperation["tool"]?.ToString() ?? activeOperation["method"]?.ToString() ?? "MCP request") +
                " (" + FormatUptime(activeOperation["elapsedSeconds"]?.Value<long?>()) + ")";
            var jobs = health["jobs"] as JArray;
            RefreshTaskCenter(jobs, health);
            var leaseOwner = health["controlLease"]?["owner"]?.ToString();
            if (!string.IsNullOrWhiteSpace(leaseOwner))
                TaskCenterSummary.Text += " · controller: " + FormatClientName(leaseOwner);
            var activeJob = jobs?.FirstOrDefault(x =>
            {
                var state = x["state"]?.ToString();
                return state is "Queued" or "Running" or "Cancelling";
            });
            var activeJobText = activeJob == null ? "" :
                "; job: " + (activeJob["tool"]?.ToString() ?? "Zemax job") +
                " · " + (activeJob["state"]?.ToString() ?? "running") +
                (activeJob["progress"]?.Value<double?>() is { } progress ? " " + Math.Round(progress * 100) + "%" : "") +
                " (" + FormatUptime(activeJob["elapsedSeconds"]?.Value<long?>()) + ")" +
                (activeJob["queuePosition"]?.Value<int?>() is { } queue && queue > 0 ? " · queue " + queue : "");
            var restartCount = health["serverRestartCount"]?.Value<int>() ?? 0;
            var hardRecoveryCount = health["hardRecoveryCount"]?.Value<int>() ?? 0;
            var softTimeout = health["requestTimeoutSeconds"]?.Value<int?>();
            var hardTimeout = health["hardRecoveryTimeoutSeconds"]?.Value<int?>();
            var clientIsolation = health["clientIsolation"]?.ToString();
            var uptime = FormatUptime(health["bridgeUptimeSeconds"]?.Value<long?>());
            var authenticationRequired = health["authenticationRequired"]?.Value<bool>() == true;
            var originValidationEnabled = health["originValidationEnabled"]?.Value<bool>() == true;
            var readOnly = health["readOnly"]?.Value<bool>() == true;
            var snapshotDirectory = health["snapshotDirectory"]?.ToString();
            var lastSnapshotPath = health["lastSnapshotPath"]?.ToString();
            var lastServerError = health["lastServerError"]?.ToString();
            var reportedRoot = health["zemaxRoot"]?.ToString();
            var reportedApi = health["zosApiFiles"] as JObject;
            var loadedApi = health["loadedZosApiFiles"] as JObject;
            var reportedData = health["zemaxDataDirectory"]?.ToString();
            var activeCalls = RefreshClientDashboard(health);
            var pathDetails = FormatZemaxPaths(Installation, reportedRoot, reportedApi, loadedApi, reportedData);
            _fullDiagnostics = "MCP endpoint: reachable\n" +
                "Bridge: " + (bridgeRunning ? "running" : "not running") +
                "; MCP server: " + (serverRunning ? "running" : "not running") +
                "; uptime: " + uptime + "; restarts: " + restartCount + "; hard recoveries: " + hardRecoveryCount + "\n" +
                "ZOS-API files: " + (apiFiles ? "found" : root == null ? "remote endpoint" : "missing") +
                "; loaded: " + (apiLoaded ? "yes" : "not yet") +
                "; OpticStudio connected: " + (apiConnected ? "yes" : "not yet") +
                "; license: " + licenseStatus + "\n" +
                "Security: " + (authenticationRequired ? "Bearer token required" : "no token") +
                "; Origin validation: " + (originValidationEnabled ? "enabled" : "not reported") +
                "; lens access: " + (readOnly ? "read-only" : "read/write with pre-change snapshots") + "\n" +
                "Snapshot folder: " + (string.IsNullOrWhiteSpace(snapshotDirectory) ? "not reported" : snapshotDirectory) +
                "; latest snapshot: " + (string.IsNullOrWhiteSpace(lastSnapshotPath) ? "none this session" : lastSnapshotPath) + "\n" +
                "Transport: " + (string.IsNullOrWhiteSpace(clientIsolation) ? "session policy not reported" : clientIsolation) +
                (softTimeout.HasValue && hardTimeout.HasValue ? "; timeout " + softTimeout + "s / hard recovery " + hardTimeout + "s" : "") + "\n" +
                pathDetails + "\n" +
                "AI calls in progress: " + activeCalls + "; requests in progress: " + activeRequests + activeOperationText + activeJobText +
                (string.IsNullOrWhiteSpace(lastServerError) ? "" : "\nLast MCP server error: " + lastServerError) +
                (localBridge ? "\nLocal launcher bridge process: running" : "");
            var ready = bridgeRunning && serverRunning && apiConnected;
            ConnectionSummary.Text = (ready ? "Ready" : "Needs attention — check the status cards above") + " · " +
                (authenticationRequired ? "Token protected" : "No access token required") + " · " +
                (readOnly ? "Read-only access" : "Lens changes allowed");
            if (bridgeRunning && serverRunning) SetIndicator(McpStateDot, McpState, "Online · accepting connections", System.Windows.Media.Brushes.SeaGreen);
            else SetIndicator(McpStateDot, McpState, "Endpoint reachable, but a service is not running", System.Windows.Media.Brushes.DarkOrange);
            if (apiConnected) SetIndicator(ZosStateDot, ZosState, "Connected to OpticStudio", System.Windows.Media.Brushes.SeaGreen);
            else if (apiLoaded) SetIndicator(ZosStateDot, ZosState, "ZOS-API loaded — waiting for OpticStudio", System.Windows.Media.Brushes.DarkOrange);
            else if (root == null) SetIndicator(ZosStateDot, ZosState, "Checked on the remote Zemax computer", System.Windows.Media.Brushes.SlateGray);
            else if (apiFiles) SetIndicator(ZosStateDot, ZosState, "Files found — not loaded yet", System.Windows.Media.Brushes.DarkOrange);
            else SetIndicator(ZosStateDot, ZosState, "ZOS-API files are missing", System.Windows.Media.Brushes.IndianRed);
            if (bridgeRunning && serverRunning) _bridgeRestartAttempts = 0;
            LastStatusCheck.Text = "Checked " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            _healthReachable = false;
            TaskCenterCancel.IsEnabled = false;
            TaskCenterSummary.Text = "Service offline; Job information unavailable.";
            TaskCenterJobs.ItemsSource = null;
            _taskHistory.Clear();
            RefreshTasksPage();
            ConnectionSummary.Text = "Offline — MCP endpoint is not reachable\n" + endpoint;
            _fullDiagnostics = "MCP endpoint: not reachable\n" +
                "ZOS-API files: " + (apiFiles ? "found" : root == null ? "remote endpoint" : "missing") +
                (localBridge ? "\nLocal launcher bridge process is running, but the HTTP health check failed: " + ex.Message : "\n" + ex.Message);
            SetIndicator(McpStateDot, McpState, "Offline — endpoint cannot be reached", System.Windows.Media.Brushes.IndianRed);
            if (root == null) SetIndicator(ZosStateDot, ZosState, "Status is available only from the Zemax computer", System.Windows.Media.Brushes.SlateGray);
            else if (apiFiles) SetIndicator(ZosStateDot, ZosState, "Files found — service is unavailable", System.Windows.Media.Brushes.DarkOrange);
            else SetIndicator(ZosStateDot, ZosState, "ZOS-API files are missing", System.Windows.Media.Brushes.IndianRed);
            RefreshClientMenuIndicators();
            SetIndicator(AiStateDot, AiState, "AI activity unavailable while offline", System.Windows.Media.Brushes.SlateGray);
            LastStatusCheck.Text = "Offline · retrying automatically";
        }
        finally { _refreshingStatus = false; }
    }
    private void SetIndicatorsChecking()
    {
        SetIndicator(McpStateDot, McpState, "Checking service…", System.Windows.Media.Brushes.DarkOrange);
        SetIndicator(ZosStateDot, ZosState, "Checking OpticStudio…", System.Windows.Media.Brushes.DarkOrange);
        SetIndicator(AiStateDot, AiState, "Checking recent MCP activity…", System.Windows.Media.Brushes.DarkOrange);
    }
    private static void SetIndicator(System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock label, string text, System.Windows.Media.Brush brush)
    {
        if (!ReferenceEquals(dot.Fill, brush)) dot.Fill = brush;
        if (label.Text != text) label.Text = text;
        if (!ReferenceEquals(label.Foreground, brush)) label.Foreground = brush;
    }
    private int RefreshClientDashboard(JObject? health, bool refreshSetup = true)
    {
        IReadOnlyCollection<ClientConfigurationStatus>? clientStatuses = null;
        if (refreshSetup)
        {
            clientStatuses = Configurator.GetClientStatuses(McpUrl, McpToken);
            RefreshClientMenuIndicators(clientStatuses);
        }
        if (health != null)
        {
            var activeRequests = health["activeRequests"]?.Value<int>() ?? 0;
            var operation = (health["activeOperations"] as JArray)?.FirstOrDefault();
            if (activeRequests > 0)
            {
                var client = FormatClientName(operation?["client"]?.ToString() ?? health["lastClient"]?.ToString());
                var tool = operation?["tool"]?.ToString() ?? health["lastTool"]?.ToString() ?? "MCP tool";
                var elapsed = operation?["elapsedSeconds"]?.Value<long?>();
                if (elapsed == null && DateTimeOffset.TryParse(operation?["startedAt"]?.ToString(), out var startedAt))
                    elapsed = Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds);
                SetIndicator(AiStateDot, AiState,
                    "Calling now: " + client + " · " + tool + " (" + FormatUptime(elapsed) + ")" +
                    (activeRequests > 1 ? " · +" + (activeRequests - 1) + " queued/active" : ""),
                    System.Windows.Media.Brushes.SeaGreen);
                return activeRequests;
            }

            var lastClient = health["lastClient"]?.ToString();
            var lastTool = health["lastTool"]?.ToString() ?? health["activity"]?["lastTool"]?.ToString();
            if (!string.IsNullOrWhiteSpace(lastClient) && !string.Equals(lastClient, "None yet", StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(health["lastRequestAt"]?.ToString(), out var lastAt))
            {
                var age = DateTimeOffset.UtcNow - lastAt;
                SetIndicator(AiStateDot, AiState,
                    "Last call: " + FormatClientName(lastClient) +
                    (string.IsNullOrWhiteSpace(lastTool) ? "" : " · " + lastTool) +
                    " · " + FormatTimeAgo(age),
                    age < TimeSpan.FromMinutes(5) ? System.Windows.Media.Brushes.SteelBlue : System.Windows.Media.Brushes.SlateGray);
                return 0;
            }
        }
        if (!refreshSetup) return 0;
        clientStatuses ??= Configurator.GetClientStatuses(McpUrl, McpToken);
        if (clientStatuses.Any(x => x.Configured)) SetIndicator(AiStateDot, AiState, clientStatuses.Count(x => x.Configured) + " configured · waiting for a call", System.Windows.Media.Brushes.DarkOrange);
        else if (clientStatuses.Any(x => x.Detected)) SetIndicator(AiStateDot, AiState, clientStatuses.Count(x => x.Detected) + " detected · setup needed", System.Windows.Media.Brushes.DarkOrange);
        else SetIndicator(AiStateDot, AiState, "No AI client call in progress", System.Windows.Media.Brushes.SlateGray);
        return 0;
    }
    private void RefreshClientMenuIndicators(IReadOnlyCollection<ClientConfigurationStatus>? statuses = null)
    {
        statuses ??= Configurator.GetClientStatuses(McpUrl, McpToken);
        SetClientMenuIndicator(statuses, "Codex", CodexConfigDot, CodexConfigState);
        SetClientMenuIndicator(statuses, "Claude Desktop", ClaudeConfigDot, ClaudeConfigState);
        SetClientMenuIndicator(statuses, "Cursor", CursorConfigDot, CursorConfigState);
        SetClientMenuIndicator(statuses, "Google Antigravity", AntigravityConfigDot, AntigravityConfigState);
        SetClientMenuIndicator(statuses, "Kimi Code", KimiConfigDot, KimiConfigState);
        SetClientMenuIndicator(statuses, "WorkBuddy", WorkBuddyConfigDot, WorkBuddyConfigState);
        SetClientMenuIndicator(statuses, "VS Code / Copilot", VsCodeConfigDot, VsCodeConfigState);
    }
    private static void SetClientMenuIndicator(IEnumerable<ClientConfigurationStatus> statuses, string name,
        System.Windows.Shapes.Ellipse dot, System.Windows.Controls.TextBlock label)
    {
        var status = statuses.First(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var brush = status.Configured ? System.Windows.Media.Brushes.SeaGreen
            : status.Detected ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.SlateGray;
        if (!ReferenceEquals(dot.Fill, brush)) dot.Fill = brush;
        if (!ReferenceEquals(label.Foreground, brush)) label.Foreground = brush;
        var statusText = status.Configured ? "Configured" : status.Detected ? "Setup needed" : "Not detected";
        if (label.Text != statusText) label.Text = statusText;
        var tooltip = name + ": " + statusText.ToLowerInvariant();
        if (!string.Equals(dot.ToolTip as string, tooltip, StringComparison.Ordinal)) dot.ToolTip = tooltip;
    }
    private static string FormatClientName(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "AI client";
        if (identity!.StartsWith("token:", StringComparison.OrdinalIgnoreCase)) return "authenticated client";
        var name = identity.StartsWith("client:", StringComparison.OrdinalIgnoreCase) ? identity.Substring(7) : identity;
        var suffix = name.IndexOfAny(new[] { '@', '|' });
        if (suffix >= 0) name = name.Substring(0, suffix);
        return string.IsNullOrWhiteSpace(name) || name.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? "AI client" : name;
    }
    private static string FormatTimeAgo(TimeSpan age) =>
        age.TotalSeconds < 10 ? "just now" :
        age.TotalMinutes < 1 ? (int)Math.Max(0, age.TotalSeconds) + "s ago" :
        age.TotalHours < 1 ? (int)age.TotalMinutes + "m ago" :
        age.TotalDays < 1 ? (int)age.TotalHours + "h ago" :
        (int)age.TotalDays + "d ago";
    private static string FormatUptime(long? totalSeconds)
    {
        if (totalSeconds == null) return "unknown";
        var value = TimeSpan.FromSeconds(Math.Max(0, totalSeconds.Value));
        return value.TotalDays >= 1 ? ((int)value.TotalDays) + "d " + value.ToString(@"hh\:mm\:ss") : value.ToString(@"hh\:mm\:ss");
    }
    private static string FormatZemaxPaths(ZemaxInstallation? installation, string? remoteRoot, JObject? remoteApi, JObject? loadedApi, string? runtimeData)
    {
        if (installation != null)
        {
            var lines = new List<string>
            {
                "OpticStudio folder: " + installation.Root + " (" + installation.DiscoverySource + ")",
                "ZOS-API: " + installation.ZosApiPath,
                "NetHelper: " + installation.NetHelperPath,
                "Detected Zemax data: " + (string.IsNullOrWhiteSpace(installation.DataDirectory) ? "not found" : installation.DataDirectory + " (" + installation.DataDirectorySource + ")")
            };
            AddLoadedApiPaths(lines, loadedApi);
            if (!string.IsNullOrWhiteSpace(runtimeData) && runtimeData != "Not reported") lines.Add("Runtime Zemax data: " + runtimeData);
            lines.Add("License setup: " + installation.LicenseEvidence);
            return string.Join("\n", lines);
        }
        if (!string.IsNullOrWhiteSpace(remoteRoot))
        {
            var lines = new List<string>
            {
                "Remote OpticStudio folder: " + remoteRoot,
                "Remote ZOS-API: " + (remoteApi?["zosApi"]?.ToString() ?? "not found"),
                "Remote NetHelper: " + (remoteApi?["netHelper"]?.ToString() ?? "not found")
            };
            AddLoadedApiPaths(lines, loadedApi);
            lines.Add("Remote Zemax data: " + (string.IsNullOrWhiteSpace(runtimeData) ? "not reported" : runtimeData));
            return string.Join("\n", lines);
        }
        return "OpticStudio and ZOS-API paths are reported by the Zemax computer after its bridge is updated.";
    }
    private static void AddLoadedApiPaths(ICollection<string> lines, JObject? loadedApi)
    {
        if (loadedApi == null) return;
        foreach (var item in new[] { ("Loaded ZOS-API", "zosApi"), ("Loaded Interfaces", "interfaces"), ("Loaded NetHelper", "netHelper") })
        {
            var path = loadedApi[item.Item2]?.ToString();
            if (!string.IsNullOrWhiteSpace(path)) lines.Add(item.Item1 + ": " + path);
        }
    }
    private static JObject GetHealth(string endpoint, string accessToken) =>
        GetEndpointJson(endpoint, accessToken, "/health", 5000);

    private static JObject GetEndpointJson(string endpoint, string accessToken, string path, int timeoutMilliseconds)
    {
        var request = (HttpWebRequest)WebRequest.Create(endpoint.TrimEnd('/') + path);
        request.Method = "GET";
        request.Timeout = timeoutMilliseconds;
        AddAuthorization(request, accessToken);
        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream());
        return JObject.Parse(reader.ReadToEnd());
    }
    private async void ScheduleStatusRefresh()
    {
        await Task.Delay(900);
        await RefreshStatusAsync();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }
    private void StopBridge()
    {
        var process = _bridge;
        _bridge = null;
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); } catch { }
    }
    private bool EnsureZosApiBootstrap(ZemaxInstallation installation)
    {
        try
        {
            // NetHelper is an Ansys library, so it is deliberately absent from
            // the public ZIP. Copy the current user's own installed copy only
            // when launching locally; ZOSAPI itself continues to load from
            // ZEMAX_ROOT through the server resolver.
            var source = installation.NetHelperPath;
            var target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZOSAPI_NetHelper.dll");
            if (!File.Exists(source)) { Report("The selected OpticStudio installation is missing ZOSAPI_NetHelper.dll in both the program folder and ZOS-API/Libraries."); return false; }
            if (!File.Exists(target) || File.GetLastWriteTimeUtc(source) != File.GetLastWriteTimeUtc(target) || new FileInfo(source).Length != new FileInfo(target).Length)
                File.Copy(source, target, true);
            return true;
        }
        catch (Exception ex)
        {
            Report("Could not prepare the local ZOS-API runtime: " + ex.Message);
            return false;
        }
    }
    private void CopyEndpoint_Click(object sender, RoutedEventArgs e) { System.Windows.Clipboard.SetText(McpUrl); Report("MCP address copied: " + McpUrl); }
    private void CopySecureSetup_Click(object sender, RoutedEventArgs e)
    {
        var setup = new JObject { ["endpoint"] = Url, ["accessToken"] = _localAccessToken }.ToString(Newtonsoft.Json.Formatting.None);
        System.Windows.Clipboard.SetText(setup);
        Report("Secure connection setup copied. Treat it like a password and paste it into the Secure setup field on the AI computer.");
    }
    private void RegenerateToken_Click(object sender, RoutedEventArgs e)
    {
        if (!LauncherDialog.Confirm(this, "Replace access token?",
            "Existing AI clients will stop connecting until they are configured with the new token.", "Replace token")) return;
        _localAccessToken = GenerateAccessToken();
        SaveSettings();
        if (!IsRemoteEndpointConfigured)
        {
            StopBridge();
            if (Installation != null) StartBridge();
        }
        RefreshClientDashboard(null);
        Report("A new access token was created. Copy secure setup and reconfigure AI clients.");
    }
    private bool TryApplySecureSetup(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith("{", StringComparison.Ordinal)) return false;
        try
        {
            var setup = JObject.Parse(text);
            var endpoint = setup["endpoint"]?.ToString();
            var token = setup["accessToken"]?.ToString();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(token)) return false;
            _remoteEndpoint = uri.ToString().TrimEnd('/');
            _remoteAccessToken = token!;
            RemoteSecureSetup.Text = "";
            UpdateRemoteSetupStatus();
            Report("Secure connection setup accepted for " + _remoteEndpoint + ".");
            return true;
        }
        catch { return false; }
    }
    private void UpdateRemoteSetupStatus()
    {
        if (!IsRemoteEndpointConfigured)
        {
            RemoteSetupDot.Fill = System.Windows.Media.Brushes.SlateGray;
            RemoteSetupStatus.Foreground = System.Windows.Media.Brushes.SlateGray;
            RemoteSetupStatus.Text = "Local MCP service selected.";
            return;
        }
        var endpoint = new Uri(_remoteEndpoint);
        RemoteSetupDot.Fill = System.Windows.Media.Brushes.SeaGreen;
        RemoteSetupStatus.Foreground = System.Windows.Media.Brushes.SeaGreen;
        RemoteSetupStatus.Text = "Remote endpoint active: " + endpoint.Host + ":" + endpoint.Port + " · token protected for this Windows user.";
    }
    private static string GenerateAccessToken()
    {
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var diagnostics = "Zemax MCP diagnostics — " + DateTimeOffset.Now.ToString("O") + "\r\n\r\n" +
                          _fullDiagnostics + "\r\n\r\nLauncher messages:\r\n" + Status.Text;
        System.Windows.Clipboard.SetText(diagnostics);
        Report("Connection diagnostics copied to the clipboard.");
    }
    private async void CheckConnection_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = McpUrl;
        var token = McpToken;
        Report("Checking Host/Worker/ZOS-API connection...");
        try { Report(await Task.Run(() => CheckConnectionHealth(endpoint, token))); }
        catch (Exception ex) { Report("Connection/authorization check failed: " + ex.Message); }
        await RefreshStatusAsync();
    }

    private async void TestMcpTools_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = McpUrl;
        var token = McpToken;
        Report("Testing MCP tools/list, read-only tool result and Tasks negotiation...");
        try { Report(await Task.Run(() => TestMcpFunctionality(endpoint, token))); }
        catch (Exception ex) { Report("MCP functionality check failed: " + ex.Message); }
        await RefreshStatusAsync();
    }

    private static string CheckConnectionHealth(string endpoint, string accessToken)
    {
        var healthEndpoint = endpoint.TrimEnd('/') + "/health";
        var request = (HttpWebRequest)WebRequest.Create(healthEndpoint);
        request.Method = "GET";
        request.Accept = "application/json";
        request.Timeout = 105000; // Cold Worker/ZOS-API bootstrap may legitimately take up to 90s.
        AddAuthorization(request, accessToken);
        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream());
        var result = JObject.Parse(reader.ReadToEnd());
        var bridge = result["bridgeRunning"]?.Value<bool>() == true;
        var worker = result["mcpServerRunning"]?.Value<bool>() == true;
        var zos = result["zosApiConnected"]?.Value<bool>() == true;
        var loaded = result["zosApiLoaded"]?.Value<bool>() == true;
        var licensed = result["licenseValidForApi"]?.Value<bool?>();
        if (!bridge) throw new InvalidOperationException("Host is reachable but reports bridgeRunning=false.");
        return "Connection check — Host: reachable; authentication: accepted; Worker: " +
            (worker ? "running" : "unavailable") + "; ZOS-API: " +
            (loaded ? "loaded" : "not loaded") + "; OpticStudio: " +
            (zos ? "connected" : "disconnected") + "; license: " +
            (result["licenseStatus"]?.ToString() ?? "not reported") +
            "; API license valid: " + (licensed.HasValue ? licensed.Value.ToString() : "not reported") +
            ". This is a health check, not a tool execution test.";
    }

    private static JObject SendMcpJsonRpc(string endpoint, string accessToken,
        string method, JObject parameters, string? routingName = null)
    {
        // 2026-07-28 stateless MCP transport: no optical edits.
        var meta = new JObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JObject
            {
                ["name"] = "zemax-launcher",
                ["version"] = "1.5.0"
            },
            ["io.modelcontextprotocol/clientCapabilities"] = new JObject
            {
                ["extensions"] = new JObject { ["io.modelcontextprotocol/tasks"] = new JObject() }
            }
        };
        parameters["_meta"] = meta;
        var message = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2741,
            ["method"] = method,
            ["params"] = parameters
        };
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "application/json, text/event-stream";
        request.Timeout = method == "tools/call" ? 105000 : 20000;
        request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        request.Headers["Mcp-Method"] = method;
        if (!string.IsNullOrEmpty(routingName)) request.Headers["Mcp-Name"] = routingName;
        AddAuthorization(request, accessToken);
        var bytes = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None));
        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        using var response = (HttpWebResponse)request.GetResponse();
        using var reader = new StreamReader(response.GetResponseStream());
        var raw = reader.ReadToEnd();
        if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Choose the actual JSON data event; ignore SSE comments/keepalives.
            var data = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line.Substring(5).Trim())
                .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(data))
                throw new InvalidDataException("MCP SSE response contained no JSON data event.");
            raw = data;
        }
        var rpc = JObject.Parse(raw);
        if (rpc["error"] is JToken error)
            throw new InvalidOperationException("MCP " + method + ": " +
                (error["message"]?.ToString() ?? "JSON-RPC error"));
        return rpc;
    }

    private static string TestMcpFunctionality(string endpoint, string accessToken)
    {
        var list = SendMcpJsonRpc(endpoint, accessToken, "tools/list", new JObject());
        var tools = list["result"]?["tools"] as JArray ??
            throw new InvalidDataException("tools/list returned no tool array.");
        if (tools.Count == 0 || !tools.Any(t => t["name"]?.ToString() == "zemax_status"))
            throw new InvalidDataException("MCP has no discoverable read-only zemax_status tool.");
        var status = SendMcpJsonRpc(endpoint, accessToken, "tools/call",
            new JObject { ["name"] = "zemax_status", ["arguments"] = new JObject() }, "zemax_status");
        if (status["result"]?["isError"]?.Value<bool>() == true ||
            !(status["result"]?["content"] is JArray content) || content.Count == 0)
            throw new InvalidDataException("Actual MCP zemax_status call returned no successful tool result.");

        // Negotiate extension explicitly. This does not create a long-running
        // optical Task: result round-trip is exercised only with an owned Task ID.
        var init = SendMcpJsonRpc(endpoint, accessToken, "initialize",
            new JObject
            {
                ["protocolVersion"] = "2026-07-28",
                ["capabilities"] = new JObject
                {
                    ["extensions"] = new JObject { ["io.modelcontextprotocol/tasks"] = new JObject() }
                },
                ["clientInfo"] = new JObject { ["name"] = "zemax-launcher", ["version"] = "1.5.0" }
            });
        var tasks = init["result"]?["capabilities"]?["extensions"]?["io.modelcontextprotocol/tasks"] != null;
        return "MCP functional test PASS — tools/list: " + tools.Count +
            " tools; real read-only zemax_status: result returned; 2026-07-28 initialize: success; " +
            "official Tasks advertised: " + (tasks ? "yes" : "no") +
            ". A completed Task result requires an owned Task ID in the Tasks page; this test does not start an optical Job.";
    }

    private async void TasksPageGetTask_Click(object sender, RoutedEventArgs e)
    {
        var id = TasksPageTaskId.Text?.Trim() ?? "";
        if (id.Length == 0 || id.Length > 128)
        {
            TasksPageDetail.Text = "Enter the exact MCP Task ID (not the Worker Job ID).";
            return;
        }
        var endpoint = McpUrl;
        var token = McpToken;
        try
        {
            var result = await Task.Run(() => SendMcpJsonRpc(endpoint, token,
                "tasks/get", new JObject { ["taskId"] = id }, id));
            TasksPageDetail.Text = result["result"]?.ToString(Newtonsoft.Json.Formatting.Indented) ??
                "No Task result returned.";
        }
        catch (Exception ex)
        {
            TasksPageDetail.Text = "Task result is unavailable (or not owned by this credential/client identity): " + ex.Message;
        }
    }

    private static void AddAuthorization(HttpWebRequest request, string accessToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logs = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logs);
        Process.Start(new ProcessStartInfo(logs) { UseShellExecute = true });
    }
    private void ConfigureDetected_Click(object sender, RoutedEventArgs e)
    {
        var configured = ConfigureDetectedClients();
        Report(configured.Count == 0 ? "No supported AI client was detected. Use the individual configuration buttons after installing one." : "Configured: " + string.Join(", ", configured) + ". Restart the client to connect.");
    }
    private void AiConfigMenu_Click(object sender, RoutedEventArgs e)
    {
        RefreshClientMenuIndicators();
        AiConfigButton.ContextMenu.PlacementTarget = AiConfigButton;
        AiConfigButton.ContextMenu.IsOpen = true;
    }
    private List<string> ConfigureDetectedClients()
    {
        var configured = new List<string>();
        foreach (var client in DetectedClientConfigurations())
        {
            try
            {
                client.Configure();
                configured.Add(client.Name);
            }
            catch (Exception ex)
            {
                Report("Could not configure " + client.Name + ": " + ex.Message);
            }
        }
        RefreshClientDashboard(null);
        return configured;
    }
    private void OfferFirstRunClientSetup()
    {
        if (_clientSetupPrompted || DetectedClientNames().Count == 0) return;
        _clientSetupPrompted = true;
        SaveSettings();
        var clients = Configurator.GetClientStatuses(McpUrl, McpToken)
            .Where(x => x.Detected && x.Configure != null).ToList();
        if (clients.All(x => x.Configured)) return;
        if (LauncherDialog.Confirm(this, "Connect your AI clients",
            "Configure the detected clients for this MCP endpoint. Other MCP entries will be kept. Restart each client after setup.",
            "Configure clients", clients.Select(x => (x.Name, x.Configured ? "Configured" : "Setup needed"))))
        {
            var configured = ConfigureDetectedClients();
            Report("Configured: " + string.Join(", ", configured) + ". Restart the client to connect.");
        }
    }
    private List<string> DetectedClientNames()
    {
        return DetectedClientConfigurations().Select(x => x.Name).ToList();
    }
    private List<(string Name, Action Configure)> DetectedClientConfigurations()
    {
        var clients = new List<(string, Action)>();
        foreach (var status in Configurator.GetClientStatuses(McpUrl, McpToken).Where(x => x.Detected && x.Configure != null))
            clients.Add((status.Name, () => status.Configure!(McpUrl, McpToken)));
        return clients;
    }
    private void ConfigureClient(string name, Action configure)
    {
        try { configure(); RefreshClientDashboard(null); Report(name + " configured for " + McpUrl + ". Restart the client to connect."); }
        catch (Exception ex) { Report("Could not configure " + name + ": " + ex.Message); }
    }
    private void Codex_Click(object sender, RoutedEventArgs e) => ConfigureClient("Codex", () => Configurator.ConfigureCodex(McpUrl, McpToken));
    private void Claude_Click(object sender, RoutedEventArgs e) => ConfigureClient("Claude Desktop", () => Configurator.ConfigureClaudeDesktop(McpUrl, McpToken));
    private void Cursor_Click(object sender, RoutedEventArgs e) => ConfigureClient("Cursor", () => Configurator.ConfigureCursor(McpUrl, McpToken));
    private void Antigravity_Click(object sender, RoutedEventArgs e) => ConfigureClient("Google Antigravity", () => Configurator.ConfigureAntigravity(McpUrl, McpToken));
    private void Kimi_Click(object sender, RoutedEventArgs e) => ConfigureClient("Kimi Code", () => Configurator.ConfigureKimi(McpUrl, McpToken));
    private void WorkBuddy_Click(object sender, RoutedEventArgs e) => ConfigureClient("WorkBuddy", () => Configurator.ConfigureWorkBuddy(McpUrl, McpToken));
    private void CopyGenericConfig_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(Configurator.GenericHttpJson(McpUrl, McpToken));
        Report("Generic HTTP MCP JSON copied. Paste it into an agent's MCP configuration and restart that agent.");
    }
    private void VsCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Configurator.ConfigureVsCode(McpUrl, McpToken);
            RefreshClientDashboard(null);
            Report("VS Code opened its MCP setup. Review and approve Zemax MCP there to finish configuration.");
        }
        catch (Exception ex) { Report("Could not open VS Code MCP setup: " + ex.Message); }
    }
    private void Update_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "ZemaxMCP-Launcher");
                var release = JObject.Parse(client.DownloadString("https://api.github.com/repos/joeyijun/OpticStudioMCPServer/releases/latest"));
                var releaseTag = release["tag_name"]?.ToString() ?? throw new InvalidDataException("The latest GitHub release has no version tag.");
                var releaseVersion = ParseReleaseVersion(releaseTag);
                var installedVersion = NormalizeVersion(typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0));
                if (releaseVersion <= installedVersion)
                {
                    Report("Already up to date (installed " + installedVersion.ToString(3) + ", latest " + releaseTag + ").");
                    return;
                }
                var asset = release["assets"]?.FirstOrDefault(x => x["name"]?.ToString().Equals("ZemaxMCP-win-x64.zip", StringComparison.OrdinalIgnoreCase) == true);
                var manifestAsset = release["assets"]?.FirstOrDefault(x => x["name"]?.ToString().Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase) == true);
                if (asset == null || manifestAsset == null) { Report("Latest release " + release["tag_name"] + " does not contain a signed Windows update package."); return; }
                var staging = Path.Combine(Path.GetTempPath(), "ZemaxMCP-update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var zip = Path.Combine(staging, "release.zip");
                var manifest = Path.Combine(staging, "release-manifest.json");
                Report("Downloading " + release["tag_name"] + "…");
                client.DownloadFile(asset["browser_download_url"]!.ToString(), zip);
                client.DownloadFile(manifestAsset["browser_download_url"]!.ToString(), manifest);
                UpdateManifestVerifier.Verify(File.ReadAllText(manifest), release["tag_name"]?.ToString() ?? "", "ZemaxMCP-win-x64.zip", zip);
                ZipFile.ExtractToDirectory(zip, staging);
                var install = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var updater = Path.Combine(staging, "ZemaxMCP.Updater.exe");
                if (!File.Exists(updater)) throw new FileNotFoundException("The signed update package does not contain ZemaxMCP.Updater.exe.", updater);
                var arguments = "--staging \"" + staging + "\" --install \"" + install + "\" --parent-pid " + Process.GetCurrentProcess().Id;
                Process.Start(new ProcessStartInfo(updater, arguments) { CreateNoWindow = true, UseShellExecute = false });
                Report("Update downloaded. Restarting with " + release["tag_name"] + "…");
                System.Windows.Application.Current.Shutdown();
            }
        }
        catch (Exception ex) { Report("Could not check GitHub releases: " + ex.Message); }
    }
    internal static Version ParseReleaseVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var suffix = value.IndexOf('-');
        if (suffix >= 0) value = value.Substring(0, suffix);
        if (!Version.TryParse(value, out var parsed)) throw new InvalidDataException("The release tag is not a supported version: " + tag);
        return NormalizeVersion(parsed);
    }
    private static Version NormalizeVersion(Version version) =>
        new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    private void Report(string text) => Status.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "launcher-settings.json");
    private string? ReadSetting(string key)
    {
        try { return LauncherSettingsStore.Load(SettingsPath)[key]?.ToString(); }
        catch { return null; }
    }
    private void LoadSettings()
    {
        try
        {
            var settings = LauncherSettingsStore.Load(SettingsPath);
            Port.Text = settings["port"]?.ToString() ?? Port.Text;
            _remoteEndpoint = settings["remoteEndpoint"]?.ToString() ?? "";
            _remoteAccessToken = UnprotectSecret(settings["remoteTokenProtected"]?.ToString());
            _localAccessToken = UnprotectSecret(settings["localTokenProtected"]?.ToString());
            if (string.IsNullOrWhiteSpace(_localAccessToken)) _localAccessToken = GenerateAccessToken();
            ShareOnLan.IsChecked = settings["shareOnLan"]?.Value<bool>() ?? false;
            ReadOnlyMode.IsChecked = settings["readOnly"]?.Value<bool>() ?? false;
            OfficialTasks.IsChecked = OfficialTasksSettings.IsEnabled(settings["enableOfficialTasks"]);
            SelectToolsetProfile(settings["toolsetProfile"]?.ToString());
            StartOnLogin.IsChecked = settings["startOnLogin"]?.Value<bool>() ?? false;
            var material = settings["windowMaterial"]?.ToString() ?? "mica";
            foreach (System.Windows.Controls.ComboBoxItem item in MaterialChoice.Items)
                if (item.Tag?.ToString() == material) MaterialChoice.SelectedItem = item;
            _clientSetupPrompted = settings["clientSetupPrompted"]?.Value<bool>() ?? false;
            UpdateRemoteSetupStatus();
        }
        catch
        {
            _settingsLoadFailed = true; // Preserve an unreadable preference file rather than overwrite it with defaults.
            _localAccessToken = GenerateAccessToken();
            _remoteEndpoint = "";
            _remoteAccessToken = "";
            UpdateRemoteSetupStatus();
        }
    }
    private void SaveSettings()
    {
        if (!_windowLoaded || _settingsLoadFailed) return;
        try
        {
            LauncherSettingsStore.Save(SettingsPath, new JObject
            {
                ["zemaxRoot"] = Installation?.Root ?? "",
                ["port"] = Port.Text,
                ["remoteEndpoint"] = _remoteEndpoint,
                ["localTokenProtected"] = ProtectSecret(_localAccessToken),
                ["remoteTokenProtected"] = ProtectSecret(_remoteAccessToken),
                ["shareOnLan"] = ShareOnLan.IsChecked == true,
                ["readOnly"] = ReadOnlyMode.IsChecked == true,
                ["enableOfficialTasks"] = OfficialTasks.IsChecked == true,
                ["toolsetProfile"] = SelectedToolsetProfile,
                ["startOnLogin"] = StartOnLogin.IsChecked == true,
                ["windowMaterial"] = SelectedMaterial,
                ["clientSetupPrompted"] = _clientSetupPrompted
            });
        }
        catch { /* Preferences are non-essential. */ }
    }
    private void SelectToolsetProfile(string? value)
    {
        foreach (var item in ToolsetProfile.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                ToolsetProfile.SelectedItem = item;
                return;
            }
        }
        ToolsetProfile.SelectedIndex = 4;
    }
    private static string ProtectSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
    private static string UnprotectSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
    private static string GetLanAddress() => Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))?.ToString() ?? "127.0.0.1";
    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(2500, "Zemax MCP is still running", "Use the tray icon to reopen it. Choose Exit to stop the MCP service.", Forms.ToolTipIcon.Info);
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        SystemEvents.UserPreferenceChanged -= AppearancePreferenceChanged;
        SystemEvents.SessionSwitch -= AppearanceSessionChanged;
        _activityTimer.Stop();
        StopBridge();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}

internal static class FirewallRule
{
    public static bool TryEnsure(int port)
    {
        try
        {
            var rule = "Zemax MCP HTTP " + port;
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var firewall = "netsh advfirewall firewall add rule name=\"" + rule + "\" dir=in action=allow protocol=TCP localport=" + port + " profile=private";
            var urlAcl = "netsh http add urlacl url=http://+:" + port + "/mcp/ user=\"" + user + "\"";
            // cmd.exe lets one UAC confirmation configure both HTTP.SYS and the
            // private-network firewall rule. Existing URL ACLs are harmless.
            var arguments = "/c \"" + firewall + " & " + urlAcl + " & exit /b 0\"";
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", arguments) { Verb = "runas", UseShellExecute = true }))
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch { return false; }
    }
}

internal static class Configurator
{
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string EnvironmentPathOrDefault(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(configured) ? fallback : Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
    }
    private static string CodexHome => EnvironmentPathOrDefault("CODEX_HOME", Path.Combine(UserProfile, ".codex"));
    private static string CodexPath => Path.Combine(CodexHome, "config.toml");
    private static string ClaudeDesktopPath => Path.Combine(AppData, "Claude", "claude_desktop_config.json");
    private static string CursorPath => Path.Combine(UserProfile, ".cursor", "mcp.json");
    // Antigravity's current global configuration location is documented as
    // ~/.gemini/config/mcp_config.json. Keep the former location in the
    // candidate list so an established installation is updated in place.
    private static readonly string[] AntigravityConfigPaths =
    {
        Path.Combine(UserProfile, ".gemini", "config", "mcp_config.json"),
        Path.Combine(UserProfile, ".gemini", "antigravity", "mcp_config.json")
    };
    private static string AntigravityPath => AntigravityConfigPaths.FirstOrDefault(File.Exists) ?? AntigravityConfigPaths[0];
    private static string KimiHome => EnvironmentPathOrDefault("KIMI_CODE_HOME", Path.Combine(UserProfile, ".kimi-code"));
    private static string KimiPath => Path.Combine(KimiHome, "mcp.json");
    private static string WorkBuddyPath => Path.Combine(UserProfile, ".workbuddy", "mcp.json");
    private static string VsCodeDefaultPath => Path.Combine(AppData, "Code", "User", "mcp.json");
    public static readonly string[] KnownAliases = { "codex", "claude", "cursor", "antigravity", "gemini", "kimi", "workbuddy", "codebuddy", "vscode", "visual studio", "copilot" };

    public static void ConfigureClaudeDesktop(string url) => ConfigureClaudeDesktop(url, "");
    public static void ConfigureClaudeDesktop(string url, string token)
    {
        ValidateUrl(url);
        var proxy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZemaxMCP.ClientProxy.exe");
        if (!File.Exists(proxy)) throw new FileNotFoundException("The release package is missing ZemaxMCP.ClientProxy.exe, which Claude Desktop needs for a private HTTP/LAN endpoint.", proxy);
        ConfigureStdioProxyJson(ClaudeDesktopPath, proxy, url, token);
    }
    public static void ConfigureCursor(string url) => ConfigureCursor(url, "");
    public static void ConfigureCursor(string url, string token) => ConfigureJson(CursorPath, "mcpServers", url, token);
    public static void ConfigureAntigravity(string url) => ConfigureAntigravity(url, "");
    public static void ConfigureAntigravity(string url, string token)
    {
        ValidateUrl(url);
        ConfigureAntigravityJson(AntigravityPath, url, token);
    }
    public static void ConfigureKimi(string url) => ConfigureKimi(url, "");
    public static void ConfigureKimi(string url, string token) => ConfigureJson(KimiPath, "mcpServers", url, token, false, true);
    public static void ConfigureWorkBuddy(string url) => ConfigureWorkBuddy(url, "");
    public static void ConfigureWorkBuddy(string url, string token) => ConfigureJson(WorkBuddyPath, "mcpServers", url, token, false, false);

    public static List<ClientConfigurationStatus> GetClientStatuses(string expectedUrl) => GetClientStatuses(expectedUrl, "");
    public static List<ClientConfigurationStatus> GetClientStatuses(string expectedUrl, string expectedToken)
    {
        var vsCodePaths = GetVsCodeConfigPaths().ToArray();
        return new List<ClientConfigurationStatus>
        {
            new ClientConfigurationStatus("Codex", new[] { "codex" }, Directory.Exists(CodexHome), IsCodexConfigured(expectedUrl, expectedToken), CodexPath, ConfigureCodex),
            new ClientConfigurationStatus("Claude Desktop", new[] { "claude" }, Directory.Exists(Path.Combine(AppData, "Claude")), IsClaudeConfigured(expectedUrl, expectedToken), ClaudeDesktopPath, ConfigureClaudeDesktop),
            new ClientConfigurationStatus("Cursor", new[] { "cursor" }, Directory.Exists(Path.Combine(UserProfile, ".cursor")) || Directory.Exists(Path.Combine(AppData, "Cursor")) || Directory.Exists(Path.Combine(LocalAppData, "Cursor")), IsJsonConfigured(CursorPath, "mcpServers", expectedUrl, expectedToken), CursorPath, ConfigureCursor),
            new ClientConfigurationStatus("Google Antigravity", new[] { "antigravity", "gemini" }, Directory.Exists(Path.Combine(UserProfile, ".gemini")), IsAntigravityConfigured(expectedUrl, expectedToken), AntigravityPath, ConfigureAntigravity),
            new ClientConfigurationStatus("Kimi Code", new[] { "kimi" }, Directory.Exists(KimiHome), IsJsonConfigured(KimiPath, "mcpServers", expectedUrl, expectedToken), KimiPath, ConfigureKimi),
            new ClientConfigurationStatus("WorkBuddy", new[] { "workbuddy", "codebuddy" }, Directory.Exists(Path.Combine(UserProfile, ".workbuddy")) || Directory.Exists(Path.Combine(AppData, "WorkBuddy")) || Directory.Exists(Path.Combine(LocalAppData, "WorkBuddy")), IsJsonConfigured(WorkBuddyPath, "mcpServers", expectedUrl, expectedToken), WorkBuddyPath, ConfigureWorkBuddy),
            new ClientConfigurationStatus("VS Code / Copilot", new[] { "vscode", "visual studio", "copilot" }, Directory.Exists(Path.Combine(AppData, "Code")) || Directory.Exists(Path.Combine(LocalAppData, "Programs", "Microsoft VS Code")), vsCodePaths.Any(x => IsJsonConfigured(x, "servers", expectedUrl, expectedToken)), string.Join("; ", vsCodePaths), null)
        };
    }

    public static string GenericHttpJson(string url, string token) => new JObject
    {
        ["mcpServers"] = new JObject { ["zemax-mcp"] = CreateHttpEntry(url, token, true) }
    }.ToString();

    public static void ConfigureVsCode(string url, string token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The MCP endpoint must be an absolute HTTP or HTTPS address.", nameof(url));

        // VS Code owns user-profile and workspace configuration locations. Its documented
        // installation URI opens the native review/trust flow and prevents this launcher
        // from overwriting an unknown profile's mcp.json file.
        var server = new JObject
        {
            ["name"] = "zemax-mcp",
            ["type"] = "http",
            ["url"] = endpoint.AbsoluteUri
        };
        AddHeaders(server, token);
        var installUri = "vscode:mcp/install?" + Uri.EscapeDataString(server.ToString(Newtonsoft.Json.Formatting.None));
        Process.Start(new ProcessStartInfo(installUri) { UseShellExecute = true });
    }

    public static void ConfigureJson(string path, string property, string url, string token, bool includeType = true, bool includeKimiTimeouts = false)
    {
        ValidateUrl(url);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var servers = root[property] as JObject;
        if (servers == null)
        {
            servers = new JObject();
            root[property] = servers;
        }
        var entry = CreateHttpEntry(url, token, includeType);
        if (includeKimiTimeouts)
        {
            entry["startupTimeoutMs"] = 60000;
            entry["toolTimeoutMs"] = 300000;
        }
        servers["zemax-mcp"] = entry;
        WriteAtomically(path, root.ToString());
    }

    private static void ConfigureAntigravityJson(string path, string url, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var servers = root["mcpServers"] as JObject;
        if (servers == null)
        {
            servers = new JObject();
            root["mcpServers"] = servers;
        }

        // Google Antigravity uses serverUrl for every remote MCP transport.
        // Do not write url/httpUrl: those legacy fields are explicitly rejected.
        var entry = new JObject { ["serverUrl"] = url };
        AddHeaders(entry, token);
        servers["zemax-mcp"] = entry;
        WriteAtomically(path, root.ToString());
    }

    private static void ConfigureStdioProxyJson(string path, string proxyPath, string url, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var servers = root["mcpServers"] as JObject;
        if (servers == null)
        {
            servers = new JObject();
            root["mcpServers"] = servers;
        }
        servers["zemax-mcp"] = new JObject
        {
            ["command"] = proxyPath,
            ["args"] = new JArray("--url", url),
            ["env"] = string.IsNullOrWhiteSpace(token) ? new JObject() : new JObject { ["ZEMAX_MCP_TOKEN"] = token }
        };
        WriteAtomically(path, root.ToString());
    }

    public static void ConfigureCodex(string url) => ConfigureCodex(url, "");
    public static void ConfigureCodex(string url, string token)
    {
        ValidateUrl(url);
        var path = CodexPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = File.Exists(path) ? File.ReadAllText(path) : "";
        var block = "[mcp_servers.zemax]\r\nurl = \"" + url + "\"\r\n" +
                    (string.IsNullOrWhiteSpace(token) ? "" : "http_headers = { Authorization = \"Bearer " + EscapeToml(token) + "\" }\r\n");
        content = Regex.Replace(content, @"(?ms)^\[mcp_servers\.zemax\].*?(?=^\[|\z)", block);
        if (!content.Contains("[mcp_servers.zemax]")) content += (content.EndsWith("\n") || content.Length == 0 ? "" : "\r\n") + block;
        WriteAtomically(path, content);
    }

    private static JObject CreateHttpEntry(string url, string token, bool includeType)
    {
        var entry = new JObject { ["url"] = url };
        if (includeType) entry.AddFirst(new JProperty("type", "http"));
        AddHeaders(entry, token);
        return entry;
    }
    private static void AddHeaders(JObject entry, string token)
    {
        if (!string.IsNullOrWhiteSpace(token)) entry["headers"] = new JObject { ["Authorization"] = "Bearer " + token };
    }
    private static bool HasExpectedToken(JToken? entry, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)) return true;
        return string.Equals(entry?["headers"]?["Authorization"]?.ToString(), "Bearer " + expectedToken, StringComparison.Ordinal);
    }
    private static bool IsJsonConfigured(string path, string property, string expectedUrl, string expectedToken)
    {
        try
        {
            var entry = File.Exists(path) ? JObject.Parse(File.ReadAllText(path))[property]?["zemax-mcp"] : null;
            return entry != null && UrlsEqual(entry["url"]?.ToString(), expectedUrl) && HasExpectedToken(entry, expectedToken);
        }
        catch { return false; }
    }
    private static bool IsAntigravityConfigured(string expectedUrl, string expectedToken)
    {
        foreach (var path in AntigravityConfigPaths)
        {
            try
            {
                var entry = File.Exists(path) ? JObject.Parse(File.ReadAllText(path))["mcpServers"]?["zemax-mcp"] : null;
                if (entry != null && UrlsEqual(entry["serverUrl"]?.ToString(), expectedUrl) && HasExpectedToken(entry, expectedToken))
                    return true;
            }
            catch { }
        }
        return false;
    }
    private static bool IsClaudeConfigured(string expectedUrl, string expectedToken)
    {
        try
        {
            var entry = File.Exists(ClaudeDesktopPath) ? JObject.Parse(File.ReadAllText(ClaudeDesktopPath))["mcpServers"]?["zemax-mcp"] : null;
            var args = entry?["args"] as JArray;
            var configuredToken = entry?["env"]?["ZEMAX_MCP_TOKEN"]?.ToString();
            var tokenMatches = string.IsNullOrWhiteSpace(expectedToken) ||
                string.Equals(configuredToken, expectedToken, StringComparison.Ordinal);
            return entry != null && string.Equals(Path.GetFileName(entry["command"]?.ToString()), "ZemaxMCP.ClientProxy.exe", StringComparison.OrdinalIgnoreCase) &&
                   args != null && args.Any(x => UrlsEqual(x?.ToString(), expectedUrl)) && tokenMatches;
        }
        catch { return false; }
    }
    private static bool IsCodexConfigured(string expectedUrl, string expectedToken)
    {
        try
        {
            if (!File.Exists(CodexPath)) return false;
            var match = Regex.Match(File.ReadAllText(CodexPath), @"(?ms)^\[mcp_servers\.zemax\]\s*(.*?)(?=^\[|\z)");
            if (!match.Success) return false;
            var url = Regex.Match(match.Groups[1].Value, "(?m)^url\\s*=\\s*[\"']([^\"']+)[\"']").Groups[1].Value;
            if (!UrlsEqual(url, expectedUrl)) return false;
            if (string.IsNullOrWhiteSpace(expectedToken)) return true;
            var authorization = Regex.Match(match.Groups[1].Value, "Authorization\\s*=\\s*[\"']Bearer\\s+([^\"']+)[\"']").Groups[1].Value;
            return string.Equals(authorization, expectedToken, StringComparison.Ordinal);
        }
        catch { return false; }
    }
    private static IEnumerable<string> GetVsCodeConfigPaths()
    {
        yield return VsCodeDefaultPath;
        var profiles = Path.Combine(AppData, "Code", "User", "profiles");
        if (!Directory.Exists(profiles)) yield break;
        string[] profileFolders;
        try { profileFolders = Directory.GetDirectories(profiles); }
        catch { yield break; }
        foreach (var folder in profileFolders) yield return Path.Combine(folder, "mcp.json");
    }
    private static bool UrlsEqual(string? left, string? right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b)) return false;
        return a.AbsoluteUri.TrimEnd('/').Equals(b.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The MCP endpoint must be an absolute HTTP or HTTPS address.", nameof(url));
    }
    private static string EscapeToml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".zemaxmcp-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            if (File.Exists(path))
            {
                var backup = path + ".zemaxmcp.bak";
                try { File.Replace(temporary, path, backup, true); }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(path, backup, true);
                    File.Delete(path);
                    File.Move(temporary, path);
                }
            }
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}

internal sealed class ClientConfigurationStatus
{
    public ClientConfigurationStatus(string name, string[] aliases, bool detected, bool configured, string configPath, Action<string, string>? configure)
    { Name = name; Aliases = aliases; Detected = detected || configured; Configured = configured; ConfigPath = configPath; Configure = configure; }
    public string Name { get; }
    public string[] Aliases { get; }
    public bool Detected { get; }
    public bool Configured { get; }
    public string ConfigPath { get; }
    public Action<string, string>? Configure { get; }
}
