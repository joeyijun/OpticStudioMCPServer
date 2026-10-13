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
        if (LocalBridgeRunning) { Report("The current service is still running. Stop it safely before applying saved changes."); return; }
        FileStream updateGuard;
        try { updateGuard = new FileStream(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException) { Report("Installation is being updated. Wait for the updater to finish before starting the service."); return; }
        catch (UnauthorizedAccessException ex) { Report("Cannot verify installation access: " + ex.Message); return; }
        using var startGuard = updateGuard;
        if (Installation == null) { Report("Choose a detected OpticStudio installation first."); return; }
        if (!int.TryParse(Port.Text, out var port) || port < 1 || port > 65535)
        {
            Report("Port must be a number from 1 to 65535.");
            return;
        }
        if (!EnsureLocalHttps(true)) return;
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
        // Firewall setup is a user-approved configuration step, not
        // something an automatic recovery attempt should prompt for again.
        var firewallReady = automaticRestart || ShareOnLan.IsChecked != true || FirewallRule.TryEnsure(port);
        Process? process;
        try
        {
            var snapshots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "snapshots");
            var startInfo = HostLaunchPlan.Build(bridge, server, Installation.Root, HostName,
                port, ReadOnlyMode.IsChecked == true, SelectedToolsetProfile, snapshots,
                OfficialTasks.IsChecked == true, ShareOnLan.IsChecked == true, GetLanAddress(),
                true, _tlsPfxPath, _localAccessToken, _tlsPfxPassword);
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Report("Could not start the MCP bridge: " + ex.Message);
            return;
        }
        if (process == null) { Report("Windows did not create the MCP bridge process."); return; }
        _bridge = process;
        _runningEndpoint = Url;
        _runningAccessToken = _localAccessToken;
        _runningInstallation = Installation;
        ServiceSettingsNote.Visibility = Visibility.Collapsed;
        RefreshEndpoint();
        process.EnableRaisingEvents = true;
        process.Exited += async (_, _) => await Dispatcher.InvokeAsync(() => HandleBridgeExitAsync(process));
        Report("HTTPS MCP started: " + LocalEndpoint + Environment.NewLine + "Logs: " + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs") +
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
    private async void StopService()
    {
        if (IsRemoteEndpointConfigured && !LocalBridgeRunning) { Report("Remote service unchanged. Stop it safely on the OpticStudio computer."); return; }
        if (!await CanStopLocalServiceAsync()) return;
        StopBridge();
        Report("HTTPS MCP stopped.");
        RefreshEndpoint();
        SetIndicator(McpStateDot, McpState, "Stopped", System.Windows.Media.Brushes.IndianRed);
        SetIndicator(AiStateDot, AiState, "No client activity while stopped", System.Windows.Media.Brushes.SlateGray);
    }
    private void MarkConfigurationPending()
    {
        if (!_windowLoaded) return;
        ServiceSettingsNote.Text = IsRemoteEndpointConfigured
            ? "Local preferences saved. The remote service is unchanged."
            : LocalBridgeRunning
                ? "Changes saved for next Start. Stop safely when tasks finish; the running connection remains unchanged."
                : "Settings saved. Select Start to apply.";
        ServiceSettingsNote.Visibility = Visibility.Visible;
        Report(ServiceSettingsNote.Text);
    }
    private async Task<bool> CanStopLocalServiceAsync()
    {
        if (!LocalBridgeRunning) return true;
        if (_checkingStop) return false;
        var process = _bridge;
        var endpoint = _runningEndpoint;
        var token = _runningAccessToken;
        _checkingStop = true;
        try
        {
            var health = await Task.Run(() => GetHealth(endpoint, token));
            if (!ConfigurationSafety.IsIdle(health))
            {
                Report("Stop postponed: an optical operation is active or idle status cannot be verified. Cancel tasks and wait for completion before stopping.");
                return false;
            }
            return ReferenceEquals(_bridge, process);
        }
        catch
        {
            return ReferenceEquals(_bridge, process) && LauncherDialog.Confirm(this, "Force stop unavailable service?",
                "The current service cannot be checked. Force stopping may interrupt an optical operation and lose unsaved work. Prefer waiting for recovery.", "Force stop");
        }
        finally { _checkingStop = false; }
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();
    private async void ExitApplication()
    {
        if (!await CanStopLocalServiceAsync()) return;
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
}
