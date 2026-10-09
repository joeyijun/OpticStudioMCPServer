using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ZemaxMCP.DesktopShared;

internal static class DesktopShortcut
{
    // Rename only our shortcut for this exact executable. Do not create shortcuts
    // for portable users, overwrite a custom shortcut, or touch other installations.
    internal static void MigrateLegacy(string desktop, string target)
    {
        var legacy = Path.Combine(desktop, "Start Zemax MCP.lnk");
        var current = Path.Combine(desktop, "Zemax MCP.lnk");
        if (!File.Exists(legacy)) return;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            if (!MatchesTarget(shell!, legacy, target)) return;
            if (!File.Exists(current)) File.Move(legacy, current);
            else if (MatchesTarget(shell!, current, target)) File.Delete(legacy);
        }
        finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }

    private static bool MatchesTarget(object shell, string path, string target)
    {
        object shortcut = ((dynamic)shell).CreateShortcut(path);
        try
        {
            string existing = ((dynamic)shortcut).TargetPath;
            return !string.IsNullOrWhiteSpace(existing) &&
                Path.GetFullPath(existing).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
    }
}
