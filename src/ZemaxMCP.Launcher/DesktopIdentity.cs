using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZemaxMCP.Launcher;

internal static class DesktopIdentity
{
    internal const string DisplayName = "Zemax MCP";
    internal const string AppId = "ZemaxMCP.Desktop";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    // Set before constructing any windows or the NotifyIcon. Stable across
    // versions; do not rename the executable and break installers/updaters.
    internal static void Initialize()
    {
        try
        {
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
            key?.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            var icon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZemaxMCP.ico");
            if (File.Exists(icon)) key?.SetValue("IconUri", icon, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            // Corporate policy may prohibit registration. Product/file metadata
            // still provides a friendly fallback; service startup must survive.
            Trace.TraceWarning("Desktop identity could not be registered: " + ex.Message);
        }
    }
}
