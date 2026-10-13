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
    private void CopyEndpoint_Click(object sender, RoutedEventArgs e) { System.Windows.Clipboard.SetText(McpUrl); Report("MCP address copied: " + McpUrl); }
    private void CopySecureSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!LocalBridgeRunning && !EnsureLocalHttps()) return;
        var setup = new JObject { ["endpoint"] = LocalEndpoint, ["accessToken"] = LocalBridgeRunning ? _runningAccessToken : _localAccessToken };
        {
            try
            {
                using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(_tlsPfxPath, _tlsPfxPassword,
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
                // Share leaf public bytes only, NEVER PFX/password/private key.
                setup["serverCertificate"] = Convert.ToBase64String(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
                setup["certificateSha256"] = AutomaticHttps.Fingerprint(certificate);
            }
            catch (Exception ex) { Report("Could not read HTTPS certificate: " + ex.Message); return; }
        }
        System.Windows.Clipboard.SetText(setup.ToString(Newtonsoft.Json.Formatting.None));
        Report("HTTPS connection setup copied. Treat the Bearer token like a password.\nServer SHA-256: " + setup["certificateSha256"]);
    }
    private void RegenerateToken_Click(object sender, RoutedEventArgs e)
    {
        if (!LauncherDialog.Confirm(this, "Replace access token?",
            "The new token applies on next Start. Afterwards copy connection setup and reconfigure AI clients.", "Replace token")) return;
        _localAccessToken = GenerateAccessToken();
        SaveSettings();
        MarkConfigurationPending();
        RefreshClientDashboard(null);
        Report("A new access token was saved for next Start. The current connection is unchanged.");
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
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Paste complete HTTPS connection setup from the OpticStudio computer. HTTP connections are not supported.");
            if (setup["serverCertificate"] != null)
            {
                using var certificate = AutomaticHttps.ReadPublic(setup["serverCertificate"]!.ToString());
                var fingerprint = AutomaticHttps.Fingerprint(certificate);
                if (!string.Equals(fingerprint, setup["certificateSha256"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Certificate fingerprint does not match setup.");
                if (!AutomaticHttps.IsTrusted(certificate))
                {
                    AutomaticHttps.TrustCurrentUser(certificate);
                }
            }
            _remoteEndpoint = uri.ToString().TrimEnd('/');
            _remoteAccessToken = token!;
            RemoteSecureSetup.Text = "";
            UpdateRemoteSetupStatus();
            Report("HTTPS connection setup accepted for " + _remoteEndpoint + ".");
            return true;
        }
        catch (Exception ex) { Report("HTTPS connection setup could not be accepted: " + ex.Message); return false; }
    }
    private void UpdateRemoteSetupStatus()
    {
        if (!IsRemoteEndpointConfigured)
        {
            RemoteSetupDot.Fill = System.Windows.Media.Brushes.SlateGray;
            RemoteSetupStatus.Foreground = System.Windows.Media.Brushes.SlateGray;
            RemoteSetupStatus.Text = "Using this computer. Paste setup and select Connect to use remote OpticStudio.";
            SwitchToLocalButton.Visibility = Visibility.Collapsed;
            return;
        }
        var endpoint = new Uri(_remoteEndpoint);
        SwitchToLocalButton.Visibility = Visibility.Visible;
        var unencrypted = endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback;
        RemoteSetupDot.Fill = unencrypted ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.SeaGreen;
        RemoteSetupStatus.Foreground = unencrypted ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.SeaGreen;
        RemoteSetupStatus.Text = "Remote OpticStudio selected: " + endpoint.Host + ":" + endpoint.Port +
            (unencrypted ? " · WARNING: plaintext HTTP exposes the Bearer token in transit; use HTTPS." :
             endpoint.Scheme == Uri.UriSchemeHttps ? " · HTTPS. No local service needed." :
             " · local loopback HTTP.");
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
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TestConnectionButton.IsEnabled) return;
        var endpoint = McpUrl;
        var token = McpToken;
        TestConnectionButton.IsEnabled = false;
        TestConnectionButton.Content = "Testing…";
        Report("Checking Host, authentication, Worker, OpticStudio and license status...");
        try { Report(await Task.Run(() => CheckConnectionHealth(endpoint, token))); }
        catch (Exception ex) { Report("Connection check failed: " + ex.Message); }
        finally
        {
            TestConnectionButton.Content = "Check connection";
            TestConnectionButton.IsEnabled = true;
        }
        await RefreshStatusAsync();
    }

    private async void TestMcpTools_Click(object sender, RoutedEventArgs e)
    {
        if (!TestMcpToolsButton.IsEnabled) return;
        var endpoint = McpUrl;
        var token = McpToken;
        TestMcpToolsButton.IsEnabled = false;
        TestMcpToolsButton.Content = "Testing…";
        Report("Testing actual MCP tools/list, a read-only tool call and official Tasks discovery...");
        try { Report(await Task.Run(() => TestMcpFunctionality(endpoint, token))); }
        catch (Exception ex) { Report("MCP functional test failed: " + ex.Message); }
        finally
        {
            TestMcpToolsButton.Content = "Test MCP tools";
            TestMcpToolsButton.IsEnabled = true;
        }
    }

    private static string CheckConnectionHealth(string endpoint, string token) =>
        McpDiagnosticsClient.CheckConnectionHealth(endpoint, token);

    private static JObject SendMcpJsonRpc(string endpoint, string token,
        string method, JObject parameters, string? routingName = null) =>
        McpDiagnosticsClient.SendMcpJsonRpc(endpoint, token, method, parameters, routingName);

    private static string TestMcpFunctionality(string endpoint, string token) =>
        McpDiagnosticsClient.TestMcpFunctionality(endpoint, token);

    private async void TasksPageGetTask_Click(object sender, RoutedEventArgs e)
    {
        TasksPageDetailsSection.IsExpanded = true;
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
}
