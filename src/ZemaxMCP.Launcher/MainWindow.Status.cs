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
    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();
    private async Task RefreshActivityAsync()
    {
        if (_refreshingActivity || !_healthReachable) return;
        var endpoint = McpUrl;
        var accessToken = McpToken; // WPF PasswordBox must remain on Dispatcher thread.
        if (_activityDelta.Bind(endpoint, accessToken))
        {
            _activityEndpointSupported = true;
            _activityDeltaSupported = true;
        }
        if (!_activityEndpointSupported && DateTimeOffset.UtcNow < _activityEndpointRetryAfter) return;
        _refreshingActivity = true;
        try
        {
            JObject activity;
            if (_activityDeltaSupported)
            {
                try
                {
                    var delta = await Task.Run(() => GetEndpointJson(endpoint, accessToken,
                        _activityDelta.UrlSuffix("/activity-delta"), 2000));
                    if (!_activityDelta.Matches(McpUrl, McpToken)) return; // Token changed during request.
                    _activityDelta.Update(delta["cursor"]?.ToString() ?? "");
                    if (delta["changed"]?.Value<bool>() != true) return;
                    activity = delta["activity"] as JObject ?? new JObject();
                }
                catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                {
                    _activityDeltaSupported = false;
                    activity = await Task.Run(() => GetEndpointJson(endpoint, accessToken, "/activity", 2000));
                }
            }
            else activity = await Task.Run(() => GetEndpointJson(endpoint, accessToken, "/activity", 2000));
            _activityEndpointSupported = true;
            if (_healthReachable && _activityDelta.Matches(McpUrl, McpToken))
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
    private async Task RefreshJobsDeltaAsync()
    {
        if (!_healthReachable || _refreshingJobsDelta || _refreshingStatus) return;
        var endpoint = McpUrl;
        var token = McpToken; // PasswordBox must be read ONLY on the WPF thread.
        if (_jobsDelta.Bind(endpoint, token))
            _jobsDeltaSupported = true;
        if (!_jobsDeltaSupported && DateTimeOffset.UtcNow < _jobsDeltaRetryAfter) return;
        _refreshingJobsDelta = true;
        try
        {
            JObject response;
            try
            {
                response = await Task.Run(() => GetEndpointJson(endpoint, token,
                    _jobsDelta.UrlSuffix("/jobs-delta"), 2500));
            }
            catch (WebException error) when ((error.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                // Back-level Hosts keep the 5-second full /health polling path.
                _jobsDeltaSupported = false;
                _jobsDeltaRetryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
                return;
            }
            if (!_jobsDelta.Matches(McpUrl, McpToken)) return; // No stale cross-credential update.
            _jobsDelta.Update(response["cursor"]?.ToString() ?? "");
            _jobsDeltaSupported = true;
            if (response["changed"]?.Value<bool>() != true) return;
            var snapshot = response["snapshot"] as JObject ??
                throw new InvalidDataException("Changed Jobs delta must include a snapshot.");
            var worker = new JObject { ["workerGeneration"] = snapshot["workerGeneration"]?.DeepClone() };
            var presentationHealth = new JObject
            {
                ["tasks"] = snapshot["tasks"]?.DeepClone() ?? new JArray(),
                ["worker"] = worker,
                ["clientIsolation"] = snapshot["ownerScoped"]?.Value<bool>() == true ? "scoped" : "shared/local"
            };
            RefreshTaskCenter(snapshot["jobs"] as JArray, presentationHealth);
        }
        catch (WebException)
        {
            // The 5-second full health path owns connection/offline warnings.
        }
        catch (Exception)
        {
            _jobsDelta.ForceFullRefresh(); // Re-fetch a complete owner-scoped snapshot.
        }
        finally { _refreshingJobsDelta = false; }
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshingStatus) return;
        _refreshingStatus = true;
        var currentInstallation = LocalBridgeRunning ? _runningInstallation : Installation;
        var root = currentInstallation?.Root;
        var endpoint = McpUrl;
        // Capture UI-owned values before switching to the worker thread.  In
        // remote mode McpToken reads the PasswordBox, which must only ever be
        // accessed on this WPF dispatcher thread.
        var accessToken = McpToken;
        var apiFiles = currentInstallation?.ApiFilesPresent == true;
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
            var workerBusy = health["workerBusy"]?.Value<bool>() == true;
            var statusFresh = health["statusFresh"]?.Value<bool>() ?? true;
            var statusAge = health["statusAgeSeconds"]?.Value<double?>();
            var lastKnownStatus = health["lastKnownStatus"]?.Value<bool>() ?? true;
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
            var pathDetails = FormatZemaxPaths(currentInstallation, reportedRoot, reportedApi, loadedApi, reportedData);
            _fullDiagnostics = "MCP endpoint: reachable\n" +
                "Launcher build: " + ZemaxMCP.Build.BuildIdentity.Read(typeof(MainWindow).Assembly).InformationalVersion + "\n" +
                "Host build: " + (health["build"]?["informationalVersion"]?.ToString() ?? "not reported") +
                "; Worker build: " + (health["workerInformationalVersion"]?.ToString() ?? "not reported") + "\n" +
                "HTTPS certificate: " + (health["tlsCertificate"]?["status"]?.ToString() ?? "not reported") +
                "; expires: " + (health["tlsCertificate"]?["notAfterUtc"]?.ToString() ?? "not reported") +
                "; days remaining: " + (health["tlsCertificate"]?["daysRemaining"]?.ToString() ?? "unknown") + "\n" +
                "Server SHA-256: " + (health["tlsCertificate"]?["fingerprintSha256"]?.ToString() ?? "not reported") + "\n" +
                "Host queue: " + (health["requestQueue"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "not reported") + "\n" +
                "Bridge: " + (bridgeRunning ? "running" : "not running") +
                "; MCP server: " + (serverRunning ? "running" : "not running") +
                "; uptime: " + uptime + "; restarts: " + restartCount + "; hard recoveries: " + hardRecoveryCount + "\n" +
                "ZOS-API files: " + (apiFiles ? "found" : root == null ? "remote endpoint" : "missing") +
                "; loaded: " + (apiLoaded ? "yes" : "not yet") +
                "; OpticStudio connected: " + (apiConnected ? "yes" : (workerBusy && !lastKnownStatus ? "unknown while busy" : "not yet")) +
                "; license: " + licenseStatus +
                "; status: " + (statusFresh ? "fresh" : workerBusy ? "cached while Worker is busy" : "last known / unavailable") +
                (statusAge.HasValue ? " (validated " + Math.Round(statusAge.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s ago)" : " (never validated)") + "\n" +
                "Security: " + (authenticationRequired ? "Bearer token required" : "no token") +
                "; network encryption: " + TransportSecurityDisplay.Explain(
                    endpoint, health["tlsEnabled"]?.Value<bool>() == true) +
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
            if (health["tlsCertificate"]?["renewalRecommended"]?.Value<bool>() == true)
                ServiceSettingsNote.Text = "HTTPS certificate expires soon. Stop / Start safely on the OpticStudio computer to renew, then copy new setup to AI computers.";
            if (health["tlsCertificate"]?["renewalRecommended"]?.Value<bool>() == true)
                ServiceSettingsNote.Visibility = Visibility.Visible;
            ConnectionSummary.Text = (workerBusy
                ? "OpticStudio busy — " + (lastKnownStatus ? "last verified status shown" : "live license check deferred")
                : ready ? "Ready" : "Needs attention — check the status cards above") + " · " +
                (authenticationRequired ? "Token protected" : "No access token required") + " · " +
                (readOnly ? "Read-only access" : "Lens changes allowed");
            if (bridgeRunning && serverRunning) SetIndicator(McpStateDot, McpState, "Online · accepting connections", System.Windows.Media.Brushes.SeaGreen);
            else SetIndicator(McpStateDot, McpState, "Endpoint reachable, but a service is not running", System.Windows.Media.Brushes.DarkOrange);
            if (workerBusy && !lastKnownStatus)
                SetIndicator(ZosStateDot, ZosState, "OpticStudio busy — connection status pending", System.Windows.Media.Brushes.DarkOrange);
            else if (apiConnected) SetIndicator(ZosStateDot, ZosState, workerBusy
                ? "OpticStudio busy — previously connected" : "Connected to OpticStudio",
                System.Windows.Media.Brushes.SeaGreen);
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
            TaskCenterSummary.Text = "Service offline; Job information unavailable.";
            _taskCenter.Update(null, null);
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
    private static string FormatClientName(string? name) =>
        StatusPresentation.FormatClientName(name);
    private static string FormatTimeAgo(TimeSpan age) =>
        StatusPresentation.FormatTimeAgo(age);
    private static string FormatUptime(long? seconds) =>
        StatusPresentation.FormatUptime(seconds);
    private static string FormatZemaxPaths(ZemaxInstallation? local,
        string? root, JObject? remote, JObject? loaded, string? runtimeData) =>
        StatusPresentation.FormatZemaxPaths(local, root, remote, loaded, runtimeData);

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
}
