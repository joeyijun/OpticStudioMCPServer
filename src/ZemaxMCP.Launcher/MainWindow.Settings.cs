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
            if (Uri.TryCreate(_remoteEndpoint, UriKind.Absolute, out var previous) && previous.Scheme == Uri.UriSchemeHttp)
                _remoteEndpoint = new UriBuilder(previous) { Scheme = Uri.UriSchemeHttps, Port = previous.Port }.Uri.ToString().TrimEnd('/');
            _remoteAccessToken = UnprotectSecret(settings["remoteTokenProtected"]?.ToString());
            _localAccessToken = UnprotectSecret(settings["localTokenProtected"]?.ToString());
            if (string.IsNullOrWhiteSpace(_localAccessToken)) _localAccessToken = GenerateAccessToken();
            ShareOnLan.IsChecked = settings["shareOnLan"]?.Value<bool>() ?? false;
            ReadOnlyMode.IsChecked = settings["readOnly"]?.Value<bool>() ?? false;
            OfficialTasks.IsChecked = OfficialTasksSettings.IsEnabled(settings["enableOfficialTasks"]);
            _tlsPfxPath = settings["tlsPfxPath"]?.ToString() ?? "";
            _tlsPfxPassword = UnprotectSecret(settings["tlsPfxPasswordProtected"]?.ToString());
            SelectToolsetProfile(settings["toolsetProfile"]?.ToString());
            StartOnLogin.IsChecked = settings["startOnLogin"]?.Value<bool>() ?? false;
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
    private bool SaveSettings()
    {
        if (!_windowLoaded || _settingsLoadFailed) return false;
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
                ["enableTls"] = true,
                ["tlsPfxPath"] = _tlsPfxPath,
                ["tlsPfxPasswordProtected"] = ProtectSecret(_tlsPfxPassword),
                ["toolsetProfile"] = SelectedToolsetProfile,
                ["startOnLogin"] = StartOnLogin.IsChecked == true,
                ["windowMaterial"] = "mica",
                ["clientSetupPrompted"] = _clientSetupPrompted
            });
            return true;
        }
        catch { return false; }
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
}
