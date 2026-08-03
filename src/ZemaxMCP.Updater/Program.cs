using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ZemaxMCP.Updater;

internal static class Program
{
    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "update.log");

    public static int Main(string[] args)
    {
        string? backup = null;
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
            WaitForParent(options.ParentPid);
            backup = Path.Combine(Path.GetTempPath(), "ZemaxMCP-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            CopyDirectory(options.Install, backup, skipRuntimeData: true);
            try
            {
                ClearDirectory(options.Install, preserveRuntimeData: true);
                CopyDirectory(options.Staging, options.Install, skipRuntimeData: true,
                    excludedNames: new[] { "release.zip", "release-manifest.json" });
                var launcher = Path.Combine(options.Install, "Start-Zemax-MCP.exe");
                if (!File.Exists(launcher)) throw new FileNotFoundException("Updated launcher is missing.", launcher);
                Log("Update installed successfully.");
                if (options.Restart) Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true });
                return 0;
            }
            catch
            {
                Log("Update failed; restoring the previous installation.");
                ClearDirectory(options.Install, preserveRuntimeData: true);
                CopyDirectory(backup, options.Install, skipRuntimeData: false);
                var launcher = Path.Combine(options.Install, "Start-Zemax-MCP.exe");
                if (options.Restart && File.Exists(launcher)) Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true });
                throw;
            }
        }
        catch (Exception ex)
        {
            Log("Fatal update error: " + ex);
            return 1;
        }
        finally
        {
            try { if (!string.IsNullOrWhiteSpace(backup) && Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
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
        return new Options(Path.GetFullPath(staging), Path.GetFullPath(install), pid, restart);
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
        try { Process.GetProcessById(pid).WaitForExit(30000); }
        catch (ArgumentException) { }
    }

    private static void CopyDirectory(string source, string target, bool skipRuntimeData, IEnumerable<string>? excludedNames = null)
    {
        var excluded = new HashSet<string>(excludedNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if (excluded.Contains(Path.GetFileName(file))) continue;
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (skipRuntimeData && (name.Equals("logs", StringComparison.OrdinalIgnoreCase) || name.Equals("snapshots", StringComparison.OrdinalIgnoreCase))) continue;
            CopyDirectory(directory, Path.Combine(target, name), skipRuntimeData, excluded);
        }
    }

    private static void ClearDirectory(string directory, bool preserveRuntimeData)
    {
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        foreach (var child in Directory.GetDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (preserveRuntimeData && (name.Equals("logs", StringComparison.OrdinalIgnoreCase) || name.Equals("snapshots", StringComparison.OrdinalIgnoreCase))) continue;
            Directory.Delete(child, true);
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
        public Options(string staging, string install, int parentPid, bool restart) { Staging = staging; Install = install; ParentPid = parentPid; Restart = restart; }
        public string Staging { get; }
        public string Install { get; }
        public int ParentPid { get; }
        public bool Restart { get; }
    }
}
