using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ZemaxMCP.DesktopShared;

namespace ZemaxMCP.Installer;

public partial class MainWindow : Window
{
    private bool _installing;
    public MainWindow() { InitializeComponent(); Status.Text = "Destination: " + InstallDirectory; Closing += (_, e) => { if (_installing) e.Cancel = true; }; }
    private static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP");

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        InstallButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        try
        {
            var source = AppDomain.CurrentDomain.BaseDirectory;
            var target = InstallDirectory;
            Directory.CreateDirectory(target);
            var alreadyInstalled = string.Equals(
                Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
            if (!alreadyInstalled)
            {
                var existingLauncher = Path.Combine(target, "Start-Zemax-MCP.exe");
                var hasExistingInstall = File.Exists(existingLauncher);
                Status.Text = hasExistingInstall
                    ? "Updating the existing installation. Stopping the old launcher…"
                    : "Installing Zemax MCP…";
                await Task.Run(() =>
                {
                    if (hasExistingInstall) RunUpdater(source, target);
                    else { StopExistingProcesses(); CopyInitialInstall(source, target); }
                });
            }
            var launcher = Path.Combine(target, "Start-Zemax-MCP.exe");
            if (!File.Exists(launcher)) throw new FileNotFoundException("The release package is missing Start-Zemax-MCP.exe.");
            CreateDesktopShortcut(launcher);
            Status.Text = "Installed successfully. A desktop shortcut was created. Starting Zemax MCP…";
            Process.Start(launcher);
            _installing = false;
            Close();
        }
        catch (Exception ex) { Status.Text = "Installation failed: " + ex.Message; }
        finally { _installing = false; InstallButton.IsEnabled = true; CloseButton.IsEnabled = true; }
    }
    private static void CopyInitialInstall(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (IsPackageOnlyFile(name) || InstallRuntimeData.IsFile(name)) continue;
            InstallRuntimeData.WithRetry(() => File.Copy(file, Path.Combine(target, name), true));
        }
        foreach (var folder in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(folder);
            if (InstallRuntimeData.IsDirectory(name)) continue;
            CopyInitialInstall(folder, Path.Combine(target, name));
        }
    }

    private static bool IsPackageOnlyFile(string name) =>
        name.Equals("Install.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Portable-Install.cmd", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("release.zip", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase);

    private static void RunUpdater(string source, string target)
    {
        var updater = Path.Combine(source, "ZemaxMCP.Updater.exe");
        if (!File.Exists(updater))
            throw new FileNotFoundException("The release package is missing ZemaxMCP.Updater.exe.", updater);

        var arguments =
            "--staging \"" + source.TrimEnd(Path.DirectorySeparatorChar) +
            "\" --install \"" + target.TrimEnd(Path.DirectorySeparatorChar) +
            "\" --parent-pid 0 --restart false";
        using var process = Process.Start(new ProcessStartInfo(updater, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start ZemaxMCP.Updater.exe.");

        if (!process.WaitForExit(240000))
        {
            // Do not kill a transactional updater halfway through replacement/rollback.
            throw new TimeoutException("The updater is still running after 240 seconds. Do not start another installation; check update.log for recovery details.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "The update could not be applied or rolled back cleanly. Check %LOCALAPPDATA%\\ZemaxMCP\\update.log; any preserved recovery backup path is recorded there.");
    }
    private static void StopExistingProcesses()
    {
        foreach (var name in new[] { "Start-Zemax-MCP", "ZemaxMCP.ClientProxy", "ZemaxMCP.Host", "ZemaxMCP.Worker", "ZemaxMCP.HttpBridge", "ZemaxMCP.Server" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    string executable;
                    try { executable = process.MainModule?.FileName ?? string.Empty; }
                    catch { continue; }
                    if (!executable.StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    if (!process.WaitForExit(5000)) throw new InvalidOperationException($"Could not stop {name}. Please click Exit from its tray icon, then retry.");
                }
                finally { process.Dispose(); }
            }
        }
    }
    private static void CreateDesktopShortcut(string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        dynamic shortcut = shell.CreateShortcut(Path.Combine(desktop, "Start Zemax MCP.lnk"));
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.Description = "Start Zemax MCP HTTP bridge";
        shortcut.IconLocation = CreateShortcutIcon(Path.GetDirectoryName(target)!) + ",0";
        shortcut.Save();
        SHChangeNotify(0x00002000, 0x0005, Path.Combine(desktop, "Start Zemax MCP.lnk"), IntPtr.Zero);
        SHChangeNotify(0x08000000, 0, null, IntPtr.Zero);
    }
    internal static string CreateShortcutIcon(string install)
    {
        var source = Path.Combine(install, "ZemaxMCP.ico");
        if (!File.Exists(source)) return Path.Combine(install, "Start-Zemax-MCP.exe");
        var bytes = File.ReadAllBytes(source);
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        var directory = Path.Combine(install, "shortcut-icons");
        Directory.CreateDirectory(directory);
        var icon = Path.Combine(directory, "zemax-" + hash + ".ico");
        if (!File.Exists(icon)) InstallRuntimeData.WithRetry(() => File.WriteAllBytes(icon, bytes));
        return icon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string? item1, IntPtr item2);
    private void Close_Click(object sender, RoutedEventArgs e) { if (!_installing) Close(); }
}
