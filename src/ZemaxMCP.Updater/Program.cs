using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ZemaxMCP.DesktopShared;

namespace ZemaxMCP.Updater;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "update.log");

    public static int Main(string[] args)
    {
        string? backup = null;
        var cleanupBackup = false;
        try
        {
            var options = Parse(args);
            ValidateDirectory(options.Staging, nameof(options.Staging));
            ValidateDirectory(options.Install, nameof(options.Install));
            if (PathsEqual(options.Staging, options.Install)) throw new InvalidOperationException("Staging and install directories must be different.");
            if (IsNestedPath(options.Staging, options.Install) || IsNestedPath(options.Install, options.Staging))
                throw new InvalidOperationException("Staging and install directories must not contain one another.");
            if (!File.Exists(Path.Combine(options.Staging, "Start-Zemax-MCP.exe")))
                throw new FileNotFoundException("The staged update does not contain Start-Zemax-MCP.exe.");
            if (!File.Exists(Path.Combine(options.Install, "Start-Zemax-MCP.exe")))
                throw new FileNotFoundException("The target is not an existing Zemax MCP installation.");
            using var updateLock = new FileStream(Path.Combine(options.Install, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            WaitForParent(options.ParentPid);
            StopInstalledProcesses(options.Install, options.MaintenanceTicket);
            backup = Path.Combine(Path.GetTempPath(), "ZemaxMCP-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            CopyDirectory(options.Install, backup, skipRuntimeData: true);
            Log("Recovery backup prepared at '" + backup + "'. Runtime preferences and credentials are preserved in place.");
            try
            {
                ClearDirectory(options.Install, preserveRuntimeData: true);
                CopyDirectory(options.Staging, options.Install, skipRuntimeData: true,
                    excludedNames: new[] { "release.zip", "release-manifest.json", "Install.exe", "Portable-Install.cmd" });
                var launcher = Path.Combine(options.Install, "Start-Zemax-MCP.exe");
                if (!File.Exists(launcher)) throw new FileNotFoundException("Updated launcher is missing.", launcher);
                cleanupBackup = true;
                Log("Update installed successfully.");
                if (options.Restart) Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true });
                return 0;
            }
            catch (Exception updateError)
            {
                Log("Update failed; restoring the previous installation.");
                try
                {
                    ClearDirectory(options.Install, preserveRuntimeData: true);
                    CopyDirectory(backup, options.Install, skipRuntimeData: false);
                    cleanupBackup = true;
                    var launcher = Path.Combine(options.Install, "Start-Zemax-MCP.exe");
                    if (options.Restart && File.Exists(launcher)) Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true });
                    throw;
                }
                catch (Exception rollbackError) when (!ReferenceEquals(rollbackError, updateError))
                {
                    cleanupBackup = false;
                    var recovery = $"Automatic rollback also failed. The previous installation backup has been preserved at '{backup}'.";
                    Log(recovery + " Rollback error: " + rollbackError);
                    throw new AggregateException(recovery, updateError, rollbackError);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Fatal update error: " + ex);
            return 1;
        }
        finally
        {
            try
            {
                if (cleanupBackup && !string.IsNullOrWhiteSpace(backup) && Directory.Exists(backup))
                    Directory.Delete(backup, true);
            }
            catch { }
        }
    }

    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i += 2) values[args[i]] = args[i + 1];
        if (!values.TryGetValue("--staging", out var staging) || !values.TryGetValue("--install", out var install))
            throw new ArgumentException("Usage: ZemaxMCP.Updater --staging <directory> --install <directory> --parent-pid <pid>");
        values.TryGetValue("--parent-pid", out var pidText);
        int.TryParse(pidText, out var pid);
        var restart = !values.TryGetValue("--restart", out var restartText) || !bool.TryParse(restartText, out var parsedRestart) || parsedRestart;
        values.TryGetValue("--maintenance-ticket", out var ticket);
        if (ticket != null && !Guid.TryParseExact(ticket, "N", out _)) throw new ArgumentException("Invalid maintenance handoff ticket.");
        return new Options(Path.GetFullPath(staging), Path.GetFullPath(install), pid, restart, ticket ?? Guid.NewGuid().ToString("N"));
    }

    private static void ValidateDirectory(string path, string name)
    {
        var root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(name + " cannot be a drive root.");
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
    }

    private static bool PathsEqual(string left, string right) =>
        left.TrimEnd(Path.DirectorySeparatorChar).Equals(right.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsNestedPath(string candidate, string parent)
    {
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void WaitForParent(int pid)
    {
        if (pid <= 0) { Thread.Sleep(1500); return; }
        try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(30000)) throw new TimeoutException("The old launcher did not exit within 30 seconds."); }
        catch (ArgumentException) { }
    }

    private static void StopInstalledProcesses(string install, string maintenanceTicket)
    {
        var prefix = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var checkedProcesses = new List<Process>();
        var preparedHosts = new List<int>();
        var approvedWorkers = new HashSet<int>();
        try
        {
            foreach (var name in new[] { "Start-Zemax-MCP", "ZemaxMCP.ClientProxy", "ZemaxMCP.Host", "ZemaxMCP.Worker", "ZemaxMCP.HttpBridge", "ZemaxMCP.Server" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                string executable;
                try { executable = process.MainModule?.FileName ?? string.Empty; }
                catch { process.Dispose(); continue; } // Never stop processes whose exact path cannot be verified.
                if (string.IsNullOrWhiteSpace(executable) || !Path.GetFullPath(executable).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { process.Dispose(); continue; }
                checkedProcesses.Add(process);
            }
            // Freeze all checked Hosts before terminating ANY process. A busy/old
            // Host postpones the entire update with the installation untouched.
            foreach (var host in checkedProcesses.Where(p => p.ProcessName is "ZemaxMCP.Host" or "ZemaxMCP.HttpBridge"))
            {
                if (host.HasExited) continue;
                preparedHosts.Add(host.Id);
                var workerPid = HostMaintenanceClient.Prepare(host.Id, maintenanceTicket);
                if (workerPid > 0) approvedWorkers.Add(workerPid);
            }
            foreach (var optical in checkedProcesses.Where(p => p.ProcessName is "ZemaxMCP.Worker" or "ZemaxMCP.Server"))
                if (!optical.HasExited && !approvedWorkers.Contains(optical.Id))
                    throw new InvalidOperationException("Update postponed: a Worker cannot be verified idle by its Host. Exit the old service safely before updating.");
            foreach (var process in checkedProcesses)
            {
                if (process.HasExited) continue;
                if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
                if (!process.WaitForExit(1500)) process.Kill();
                if (!process.WaitForExit(10000)) throw new TimeoutException("An installed process did not exit: " + process.ProcessName);
            }
        }
        finally
        {
            // If preparation failed, thaw any live Hosts already prepared.
            foreach (var pid in preparedHosts)
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) HostMaintenanceClient.Resume(pid, maintenanceTicket); } catch { }
            foreach (var process in checkedProcesses) process.Dispose();
        }
    }

    private static void CopyDirectory(string source, string target, bool skipRuntimeData, IEnumerable<string>? excludedNames = null)
    {
        var excluded = new HashSet<string>(excludedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if (excluded.Contains(Path.GetFileName(file)) || (skipRuntimeData && InstallRuntimeData.IsFile(Path.GetFileName(file)))) continue;
            InstallRuntimeData.WithRetry(() => File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true));
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (skipRuntimeData && InstallRuntimeData.IsDirectory(name)) continue;
            CopyDirectory(directory, Path.Combine(target, name), skipRuntimeData, excluded);
        }
    }

    private static void ClearDirectory(string directory, bool preserveRuntimeData)
    {
        foreach (var file in Directory.GetFiles(directory))
        {
            if (preserveRuntimeData && InstallRuntimeData.IsFile(Path.GetFileName(file))) continue;
            InstallRuntimeData.WithRetry(() => File.Delete(file));
        }
        foreach (var child in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (preserveRuntimeData && InstallRuntimeData.IsDirectory(name)) continue;
            InstallRuntimeData.WithRetry(() => Directory.Delete(child, true));
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private sealed class Options
    {
        public Options(string staging, string install, int parentPid, bool restart, string maintenanceTicket) { Staging = staging; Install = install; ParentPid = parentPid; Restart = restart; MaintenanceTicket = maintenanceTicket; }
        public string MaintenanceTicket { get; }
        public string Staging { get; }
        public string Install { get; }
        public int ParentPid { get; }
        public bool Restart { get; }
    }
}
