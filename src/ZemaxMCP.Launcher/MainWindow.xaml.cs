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
    private bool _windowLoaded;
    private string _localAccessToken = "";
    private string _remoteEndpoint = "";
    private string _remoteAccessToken = "";
    private string _fullDiagnostics = "Status has not been checked yet.";
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _statusTimer;
    public MainWindow()
    {
        InitializeComponent();
        var applicationIcon = GetApplicationIcon();
        _trayIcon = new Forms.NotifyIcon { Icon = applicationIcon, Text = "Zemax MCP", Visible = true };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Zemax MCP", null, (_, _) => RestoreWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
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
        Report(hasRemoteEndpoint
            ? "Using the saved remote MCP endpoint. Local service startup is skipped."
            : installs.Count == 0
            ? "No local OpticStudio installation detected. To use this computer as an AI client, paste the MCP address from the OpticStudio computer, then click Test MCP connection and Configure installed AI clients."
            : "Starting local MCP endpoint automatically…");
        RefreshEndpoint();
        _windowLoaded = true;
        if (installs.Count > 0 && !hasRemoteEndpoint) StartBridge();
        SetIndicatorsChecking();
        _statusTimer.Start();
        RefreshClientDashboard(null);
        OfferFirstRunClientSetup();
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

    private void ShareOnLan_Changed(object sender, RoutedEventArgs e)
    {
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
    private void StartOnLogin_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartOnLogin.IsChecked == true) run?.SetValue("ZemaxMCP", "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Start-Zemax-MCP.exe") + "\"");
            else run?.DeleteValue("ZemaxMCP", false);
            SaveSettings();
        }
        catch (Exception ex) { Report("Could not change sign-in startup: " + ex.Message); }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartBridge(false);
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
        var bridge = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZemaxMCP.HttpBridge.exe");
        var server = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZemaxMCP.Server.exe");
        if (!File.Exists(bridge) || !File.Exists(server)) { Report("Release package is incomplete: ZemaxMCP.HttpBridge.exe and ZemaxMCP.Server.exe must be beside this launcher."); return; }
        if (!EnsureZosApiBootstrap(Installation)) return;
        // URL ACL/firewall setup is a user-approved configuration step, not
        // something an automatic recovery attempt should prompt for again.
        var firewallReady = automaticRestart || ShareOnLan.IsChecked != true || FirewallRule.TryEnsure(port);
        Process? process;
        try
        {
            var snapshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "snapshots");
            var startInfo = new ProcessStartInfo(bridge,
                $"--server \"{server}\" --zemax-root \"{Installation.Root}\" --host {HostName} --port {port} --read-only {(ReadOnlyMode.IsChecked == true ? "true" : "false")} --snapshot-dir \"{snapshots}\"")
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
    private void Stop_Click(object sender, RoutedEventArgs e) { StopBridge(); Report("HTTP MCP stopped."); SetIndicator(McpStateDot, McpState, "Stopped", System.Windows.Media.Brushes.IndianRed); SetIndicator(AiStateDot, AiState, "No client activity while stopped", System.Windows.Media.Brushes.SlateGray); }
    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
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
        ConnectionSummary.Text = "Checking " + endpoint + "…";
        SetIndicatorsChecking();
        try
        {
            var health = await Task.Run(() => GetHealth(endpoint, accessToken));
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
            var activeClients = RefreshClientDashboard(health);
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
                "AI clients active recently: " + activeClients + "; requests in progress: " + activeRequests + activeOperationText + activeJobText +
                (string.IsNullOrWhiteSpace(lastServerError) ? "" : "\nLast MCP server error: " + lastServerError) +
                (localBridge ? "\nLocal launcher bridge process: running" : "");
            var ready = bridgeRunning && serverRunning && apiConnected;
            ConnectionSummary.Text = (ready ? "Ready" : "Needs attention") + " — MCP " +
                (bridgeRunning && serverRunning ? "online" : "not ready") + ", OpticStudio " +
                (apiConnected ? "connected" : apiLoaded ? "waiting" : "not connected") + ", license " + licenseStatus + "\n" +
                endpoint + " · " + (authenticationRequired ? "token protected" : "unprotected") +
                " · " + (readOnly ? "read-only" : "snapshot protected") +
                " · AI active: " + activeClients + " · requests: " + activeRequests + activeOperationText + activeJobText +
                (restartCount > 0 ? " · restarts: " + restartCount : "") +
                (hardRecoveryCount > 0 ? " · hard recoveries: " + hardRecoveryCount : "") +
                (string.IsNullOrWhiteSpace(lastServerError) ? "" : "\nLast error: " + lastServerError);
            if (bridgeRunning && serverRunning) SetIndicator(McpStateDot, McpState, "Online — MCP server is accepting connections", System.Windows.Media.Brushes.SeaGreen);
            else SetIndicator(McpStateDot, McpState, "Endpoint reachable, but a service is not running", System.Windows.Media.Brushes.DarkOrange);
            if (apiConnected) SetIndicator(ZosStateDot, ZosState, "Connected to OpticStudio", System.Windows.Media.Brushes.SeaGreen);
            else if (apiLoaded) SetIndicator(ZosStateDot, ZosState, "ZOS-API loaded — waiting for OpticStudio", System.Windows.Media.Brushes.DarkOrange);
            else if (root == null) SetIndicator(ZosStateDot, ZosState, "Checked on the remote Zemax computer", System.Windows.Media.Brushes.SlateGray);
            else if (apiFiles) SetIndicator(ZosStateDot, ZosState, "Files found — not loaded yet", System.Windows.Media.Brushes.DarkOrange);
            else SetIndicator(ZosStateDot, ZosState, "ZOS-API files are missing", System.Windows.Media.Brushes.IndianRed);
            if (bridgeRunning && serverRunning) _bridgeRestartAttempts = 0;
            LastStatusCheck.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss") + " · automatic refresh every 5 seconds";
        }
        catch (Exception ex)
        {
            ConnectionSummary.Text = "Offline — MCP endpoint is not reachable\n" + endpoint;
            _fullDiagnostics = "MCP endpoint: not reachable\n" +
                "ZOS-API files: " + (apiFiles ? "found" : root == null ? "remote endpoint" : "missing") +
                (localBridge ? "\nLocal launcher bridge process is running, but the HTTP health check failed: " + ex.Message : "\n" + ex.Message);
            SetIndicator(McpStateDot, McpState, "Offline — endpoint cannot be reached", System.Windows.Media.Brushes.IndianRed);
            if (root == null) SetIndicator(ZosStateDot, ZosState, "Status is available only from the Zemax computer", System.Windows.Media.Brushes.SlateGray);
            else if (apiFiles) SetIndicator(ZosStateDot, ZosState, "Files found — service is unavailable", System.Windows.Media.Brushes.DarkOrange);
            else SetIndicator(ZosStateDot, ZosState, "ZOS-API files are missing", System.Windows.Media.Brushes.IndianRed);
            SetIndicator(AiStateDot, AiState, "Unknown until the MCP service responds", System.Windows.Media.Brushes.SlateGray);
            RefreshClientDashboard(null);
            LastStatusCheck.Text = "Last checked " + DateTime.Now.ToString("HH:mm:ss") + " · endpoint unavailable; retrying automatically";
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
        dot.Fill = brush;
        label.Text = text;
        label.Foreground = brush;
    }
    private int RefreshClientDashboard(JObject? health)
    {
        var activities = ReadClientActivities(health);
        var activeNames = new List<string>();
        var clientStatuses = Configurator.GetClientStatuses(McpUrl, McpToken);
        RefreshClientMenuIndicators(clientStatuses);
        foreach (var client in clientStatuses)
        {
            var activity = activities.FirstOrDefault(x => client.Aliases.Any(alias => x.Name.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0));
            var recent = activity != null && DateTime.Now - activity.LastRequest.ToLocalTime() < TimeSpan.FromMinutes(5);
            if (recent) activeNames.Add(client.Name);
        }
        if (activeNames.Count > 0) SetIndicator(AiStateDot, AiState, activeNames.Count + " active: " + string.Join(", ", activeNames), System.Windows.Media.Brushes.SeaGreen);
        else if (activities.Count > 0) SetIndicator(AiStateDot, AiState, "No recent AI call; " + activities.Count + " client(s) seen earlier", System.Windows.Media.Brushes.SteelBlue);
        else if (clientStatuses.Any(x => x.Configured)) SetIndicator(AiStateDot, AiState, clientStatuses.Count(x => x.Configured) + " configured · waiting for a call", System.Windows.Media.Brushes.DarkOrange);
        else if (clientStatuses.Any(x => x.Detected)) SetIndicator(AiStateDot, AiState, clientStatuses.Count(x => x.Detected) + " detected · setup needed", System.Windows.Media.Brushes.DarkOrange);
        else SetIndicator(AiStateDot, AiState, "No AI client call recorded", System.Windows.Media.Brushes.SlateGray);
        return activeNames.Count;
    }
    private void RefreshClientMenuIndicators(IReadOnlyCollection<ClientConfigurationStatus>? statuses = null)
    {
        statuses ??= Configurator.GetClientStatuses(McpUrl, McpToken);
        SetClientMenuIndicator(statuses, "Codex", CodexConfigDot, CodexConfigState);
        SetClientMenuIndicator(statuses, "Claude Desktop", ClaudeConfigDot, ClaudeConfigState);
        SetClientMenuIndicator(statuses, "Cursor", CursorConfigDot, CursorConfigState);
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
        dot.Fill = brush;
        label.Foreground = brush;
        label.Text = status.Configured ? "Configured" : status.Detected ? "Setup needed" : "Not detected";
        dot.ToolTip = name + ": " + label.Text.ToLowerInvariant();
    }
    private static List<ClientActivityView> ReadClientActivities(JObject? health)
    {
        var result = new List<ClientActivityView>();
        foreach (var item in health?["clients"] as JArray ?? new JArray())
        {
            var name = item["name"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Equals("zemax-mcp-launcher", StringComparison.OrdinalIgnoreCase)) continue;
            if (DateTime.TryParse(item["lastRequestAt"]?.ToString(), out var when))
                result.Add(new ClientActivityView(name, when, item["lastMethod"]?.ToString() ?? "request"));
        }
        if (result.Count == 0)
        {
            var name = health?["lastClient"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(name) && !name.Equals("zemax-mcp-launcher", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(health?["lastRequestAt"]?.ToString(), out var when))
                result.Add(new ClientActivityView(name, when, "request"));
        }
        return result;
    }
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
    private static JObject GetHealth(string endpoint, string accessToken)
    {
        var request = (HttpWebRequest)WebRequest.Create(endpoint.TrimEnd('/') + "/health");
        request.Method = "GET";
        request.Timeout = 5000;
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
        if (System.Windows.MessageBox.Show("Create a new access token? Existing AI clients will stop connecting until they are configured again.",
            "Regenerate Zemax MCP token", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
    private async void TestMcp_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = McpUrl;
        var accessToken = McpToken;
        Report("Testing MCP handshake: " + endpoint + "…");
        try { Report(await Task.Run(() => TestMcp(endpoint, accessToken))); }
        catch (Exception ex) { Report("MCP connection failed: " + ex.Message + Environment.NewLine + "On the OpticStudio computer, keep Start-Zemax-MCP open, start the bridge, then enable Share with a trusted LAN computer."); }
        await RefreshStatusAsync();
    }
    private static string TestMcp(string endpoint, string accessToken)
    {
        var request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "application/json, text/event-stream";
        request.Timeout = 10000;
        AddAuthorization(request, accessToken);
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":\"zemax-mcp-healthcheck\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"zemax-mcp-launcher\",\"version\":\"1.0\"}}}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream()))
        {
            var result = JObject.Parse(reader.ReadToEnd());
            var name = result["result"]?["serverInfo"]?["name"]?.ToString();
            if (response.StatusCode != HttpStatusCode.OK || string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("the endpoint did not return an MCP initialize response.");
            return "MCP connection succeeded: " + name + " responded at " + endpoint;
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
        var clients = string.Join(", ", DetectedClientNames());
        if (System.Windows.MessageBox.Show("Detected " + clients + ". Configure it to use Zemax MCP now? Existing MCP entries will be kept.", "Zemax MCP first-time setup", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
        try { return File.Exists(SettingsPath) ? JObject.Parse(File.ReadAllText(SettingsPath))[key]?.ToString() : null; }
        catch { return null; }
    }
    private void LoadSettings()
    {
        try
        {
            var settings = File.Exists(SettingsPath) ? JObject.Parse(File.ReadAllText(SettingsPath)) : new JObject();
            Port.Text = settings["port"]?.ToString() ?? Port.Text;
            _remoteEndpoint = settings["remoteEndpoint"]?.ToString() ?? "";
            _remoteAccessToken = UnprotectSecret(settings["remoteTokenProtected"]?.ToString());
            _localAccessToken = UnprotectSecret(settings["localTokenProtected"]?.ToString());
            if (string.IsNullOrWhiteSpace(_localAccessToken)) _localAccessToken = GenerateAccessToken();
            ShareOnLan.IsChecked = settings["shareOnLan"]?.Value<bool>() ?? false;
            ReadOnlyMode.IsChecked = settings["readOnly"]?.Value<bool>() ?? false;
            StartOnLogin.IsChecked = settings["startOnLogin"]?.Value<bool>() ?? false;
            _clientSetupPrompted = settings["clientSetupPrompted"]?.Value<bool>() ?? false;
            UpdateRemoteSetupStatus();
        }
        catch
        {
            _localAccessToken = GenerateAccessToken();
            _remoteEndpoint = "";
            _remoteAccessToken = "";
            UpdateRemoteSetupStatus();
        }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, new JObject
            {
                ["zemaxRoot"] = Installation?.Root ?? "",
                ["port"] = Port.Text,
                ["remoteEndpoint"] = _remoteEndpoint,
                ["localTokenProtected"] = ProtectSecret(_localAccessToken),
                ["remoteTokenProtected"] = ProtectSecret(_remoteAccessToken),
                ["shareOnLan"] = ShareOnLan.IsChecked == true,
                ["readOnly"] = ReadOnlyMode.IsChecked == true,
                ["startOnLogin"] = StartOnLogin.IsChecked == true,
                ["clientSetupPrompted"] = _clientSetupPrompted
            }.ToString());
        }
        catch { /* Preferences are non-essential. */ }
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
        StopBridge();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}

internal sealed class ClientActivityView
{
    public ClientActivityView(string name, DateTime lastRequest, string lastMethod) { Name = name; LastRequest = lastRequest; LastMethod = lastMethod; }
    public string Name { get; }
    public DateTime LastRequest { get; }
    public string LastMethod { get; }
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
    private static string KimiHome => EnvironmentPathOrDefault("KIMI_CODE_HOME", Path.Combine(UserProfile, ".kimi-code"));
    private static string KimiPath => Path.Combine(KimiHome, "mcp.json");
    private static string WorkBuddyPath => Path.Combine(UserProfile, ".workbuddy", "mcp.json");
    private static string VsCodeDefaultPath => Path.Combine(AppData, "Code", "User", "mcp.json");
    public static readonly string[] KnownAliases = { "codex", "claude", "cursor", "kimi", "workbuddy", "codebuddy", "vscode", "visual studio", "copilot" };

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
