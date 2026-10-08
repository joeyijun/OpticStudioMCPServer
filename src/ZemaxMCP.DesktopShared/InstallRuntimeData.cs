using System;
using System.IO;

namespace ZemaxMCP.DesktopShared;

internal static class InstallRuntimeData
{
    internal static bool IsFile(string name) =>
        name.Equals("launcher-settings.json", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("launcher-settings.json.bak", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("update.log", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".update.lock", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("clients.json", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDirectory(string name) =>
        name.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("snapshots", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("shortcut-icons", StringComparison.OrdinalIgnoreCase);

    internal static void WithRetry(Action action)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { action(); return; }
            catch (IOException) when (DateTime.UtcNow < deadline) { System.Threading.Thread.Sleep(150); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { System.Threading.Thread.Sleep(150); }
        }
    }
}
