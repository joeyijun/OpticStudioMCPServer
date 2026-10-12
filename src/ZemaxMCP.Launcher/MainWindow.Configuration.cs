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
    private void ZemaxVersions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        RefreshEndpoint();
        SaveSettings();
        MarkConfigurationPending();
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
    private void Port_LostFocus(object sender, RoutedEventArgs e) { RefreshEndpoint(); SaveSettings(); MarkConfigurationPending(); }
    private void RecentActivity_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer inner)
            NestedScrollWheel.Route(inner, TasksPageScroll, e);
    }
    private async void ConnectRemoteSetup_Click(object sender, RoutedEventArgs e)
    {
        var setup = RemoteSecureSetup.Text;
        if (string.IsNullOrWhiteSpace(setup)) return;
        if (LocalBridgeRunning) { Report("Stop the local service safely before switching to a remote connection."); return; }
        if (!TryApplySecureSetup(setup))
        {
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
    private void RefreshEndpoint() => Endpoint.Text = LocalEndpoint;
    private ZemaxInstallation? Installation => ZemaxVersions.SelectedItem as ZemaxInstallation;
    private string Url => "https://" +
        (ShareOnLan.IsChecked == true ? GetLanAddress() : "127.0.0.1") + ":" + Port.Text + "/mcp";
    private bool IsRemoteEndpointConfigured => Uri.TryCreate(_remoteEndpoint, UriKind.Absolute, out var remote) &&
        remote.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(_remoteAccessToken);
    private string McpUrl => IsRemoteEndpointConfigured ? _remoteEndpoint.TrimEnd('/') : LocalEndpoint;
    private string McpToken => IsRemoteEndpointConfigured ? _remoteAccessToken : LocalBridgeRunning ? _runningAccessToken : _localAccessToken;
    private string SelectedToolsetProfile => (ToolsetProfile.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "full-expert";

    private bool EnsureLocalHttps(bool startingHost = false)
    {
        if (_settingsLoadFailed) { Report("Restore saved preferences before starting HTTPS; the existing file is preserved."); return false; }
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "https");
            var address = ShareOnLan.IsChecked == true ? GetLanAddress() : "127.0.0.1";
            var setup = AutomaticHttps.Ensure(directory, _tlsPfxPath, _tlsPfxPassword, address, GetLanAddress());
            if (!startingHost && _bridge != null && !_bridge.HasExited && setup.PfxPath != _tlsPfxPath)
            { Report("The HTTPS certificate needs renewal. Stop / Start when no optical Job is active, then copy connection setup again."); return false; }
            _tlsPfxPath = setup.PfxPath;
            _tlsPfxPassword = setup.Password;
            if (!SaveSettings()) throw new IOException("HTTPS preferences could not be saved. Check access to launcher-settings.json.");
            using var certificate = AutomaticHttps.ReadPublic(setup.PublicCertificate);
            if (!AutomaticHttps.IsTrusted(certificate))
            {
                // Windows' protected Root store owns the installation warning;
                // do not stack another modal confirmation or suppress its UI.
                AutomaticHttps.TrustCurrentUser(certificate);
            }
            return true;
        }
        catch (Exception ex) { Report("HTTPS could not be prepared: " + ex.Message + " Certificate checks remain enabled."); return false; }
    }

    private void ShareOnLan_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowLoaded) return;
        RefreshEndpoint();
        SaveSettings();
        MarkConfigurationPending();
    }
    private void ReadOnlyMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowLoaded) return;
        SaveSettings();
        MarkConfigurationPending();
    }
    private void ToolsetProfile_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        SaveSettings();
        MarkConfigurationPending();
        Report("Run configuration saved for next Start: " + (ToolsetProfile.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content + ".");
    }
    private void OfficialTasks_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowLoaded) return;
        SaveSettings();
        MarkConfigurationPending();
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
}
