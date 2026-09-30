using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ZemaxMCP.Installer;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); Status.Text = "Destination: " + InstallDirectory; }
    private static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP");

    private void Install_Click(object sender, RoutedEventArgs e)
    {
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
                StopExistingProcesses();

                if (hasExistingInstall)
                {
                    Status.Text = "Updating with verified replacement and rollback…";
                    RunUpdater(source, target);
                }
                else
                {
                    CopyInitialInstall(source, target);
                }
            }
            var launcher = Path.Combine(target, "Start-Zemax-MCP.exe");
            if (!File.Exists(launcher)) throw new FileNotFoundException("The release package is missing Start-Zemax-MCP.exe.");
            CreateDesktopShortcut(launcher);
            Status.Text = "Installed successfully. A desktop shortcut was created. Starting Zemax MCP…";
            Process.Start(launcher);
            Close();
        }
        catch (Exception ex) { Status.Text = "Installation failed: " + ex.Message; }
    }
    private static void CopyInitialInstall(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (IsPackageOnlyFile(name)) continue;
            File.Copy(file, Path.Combine(target, name), true);
        }
        foreach (var folder in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(folder);
            if (name.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("snapshots", StringComparison.OrdinalIgnoreCase)) continue;
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

        if (!process.WaitForExit(120000))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("Zemax MCP update did not finish within 120 seconds.");
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
        shortcut.IconLocation = target + ",0";
        shortcut.Save();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
