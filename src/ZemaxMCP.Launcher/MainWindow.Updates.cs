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
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCancellation != null) return;
        using var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
        _updateCancellation = cancellation;
        UpdateButton.IsEnabled = false;
        CancelUpdateButton.Visibility = Visibility.Visible;
        string? staging = null;
        int? preparedHost = null;
        try
        {
            using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ZemaxMCP-Launcher");
                Report("Checking for updates…");
                var release = JObject.Parse(await UpdateDownloader.ReadMetadataAsync(client,
                    "https://api.github.com/repos/joeyijun/OpticStudioMCPServer/releases/latest", cancellation.Token));
                var releaseTag = release["tag_name"]?.ToString() ?? throw new InvalidDataException("The latest GitHub release has no version tag.");
                var releaseVersion = UpdatePackageService.ParseReleaseVersion(releaseTag);
                var installedVersion = UpdatePackageService.NormalizeVersion(typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0));
                if (releaseVersion <= installedVersion)
                {
                    Report("Already up to date (installed " + installedVersion.ToString(3) + ", latest " + releaseTag + ").");
                    return;
                }
                var asset = release["assets"]?.FirstOrDefault(x => x["name"]?.ToString().Equals("ZemaxMCP-win-x64.zip", StringComparison.OrdinalIgnoreCase) == true);
                var manifestAsset = release["assets"]?.FirstOrDefault(x => x["name"]?.ToString().Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase) == true);
                if (asset == null || manifestAsset == null) { Report("Latest release " + release["tag_name"] + " does not contain a signed Windows update package."); return; }
                staging = Path.Combine(Path.GetTempPath(), "ZemaxMCP-update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var zip = Path.Combine(staging, "release.zip");
                var manifest = Path.Combine(staging, "release-manifest.json");
                Report("Downloading " + release["tag_name"] + "…");
                var progress = new Progress<long>(bytes => Report("Downloading " + releaseTag + " · " + (bytes / 1048576.0).ToString("F1") + " MB"));
                await UpdateDownloader.DownloadFileAsync(client, asset["browser_download_url"]!.ToString(), zip, cancellation.Token, progress);
                var manifestText = await UpdateDownloader.ReadMetadataAsync(client, manifestAsset["browser_download_url"]!.ToString(), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Report("Verifying signed update…");
                await UpdatePackageService.VerifyAndExtractAsync(manifestText, releaseTag, zip, staging, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var install = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var updater = Path.Combine(staging, "ZemaxMCP.Updater.exe");
                if (!File.Exists(updater)) throw new FileNotFoundException("The signed update package does not contain ZemaxMCP.Updater.exe.", updater);
                if (LocalBridgeRunning)
                {
                    preparedHost = _bridge!.Id;
                    await Task.Run(() => ZemaxMCP.DesktopShared.HostMaintenanceClient.Prepare(preparedHost.Value, _maintenanceTicket));
                }
                cancellation.Token.ThrowIfCancellationRequested();
                var arguments = "--staging \"" + staging + "\" --install \"" + install + "\" --parent-pid " + Process.GetCurrentProcess().Id + " --maintenance-ticket " + _maintenanceTicket;
                if (Process.Start(new ProcessStartInfo(updater, arguments) { CreateNoWindow = true, UseShellExecute = false }) == null)
                    throw new IOException("Windows did not start the updater.");
                _handoffToUpdater = true;
                _exitRequested = true;
                Report("Update downloaded. Restarting with " + release["tag_name"] + "…");
                System.Windows.Application.Current.Shutdown();
            }
        }
        catch (OperationCanceledException) { Report("Update download cancelled or its 10-minute deadline expired. Current installation and service are unchanged."); }
        catch (Exception ex) { Report("Update was not applied: " + ex.Message + " Offline computers can use a downloaded package after safely exiting the service."); }
        finally
        {
            if (!_handoffToUpdater)
            {
                if (preparedHost.HasValue)
                    try { await Task.Run(() => ZemaxMCP.DesktopShared.HostMaintenanceClient.Resume(preparedHost.Value, _maintenanceTicket)); } catch { }
                if (staging != null)
                    try { await Task.Run(() => Directory.Delete(staging, true)); } catch { }
            }
            _updateCancellation = null;
            UpdateButton.IsEnabled = true;
            CancelUpdateButton.Visibility = Visibility.Collapsed;
        }
    }
}
