using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace ZemaxMCP.Launcher;

internal static class WindowMaterial
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    public static string Apply(Window window, string material)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return "Window is not ready";
        var source = HwndSource.FromHwnd(handle);
        var reason = material == "solid" ? "Solid background selected" : FallbackReason();
        try
        {
            if (reason == null)
            {
                // Documented DWM system backdrops: Windows 11 build 22621 or later.
                // Keep a normal non-layered WPF window so DWM can compose its backdrop.
                var light = 0;
                DwmSetWindowAttribute(handle, 20, ref light, sizeof(int));
                var backdrop = material == "acrylic" ? 3 : 2;
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) >= 0 &&
                    DwmExtendFrameIntoClientArea(handle, ref margins) >= 0 && source?.CompositionTarget != null)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                    window.Background = Brushes.Transparent;
                    return material == "acrylic" ? "Acrylic · Windows frosted desktop backdrop" : "Mica · Windows wallpaper-tinted backdrop";
                }
                reason = "Native material unavailable on this Windows version";
            }
            var none = 1;
            DwmSetWindowAttribute(handle, 38, ref none, sizeof(int));
            var reset = new Margins();
            DwmExtendFrameIntoClientArea(handle, ref reset);
        }
        catch (DllNotFoundException) { reason = "Windows composition unavailable"; }
        catch (EntryPointNotFoundException) { reason = "Windows composition unavailable"; }

        if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.White;
        window.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(238, 241, 246));
        return "Solid · " + reason;
    }

    private static string? FallbackReason()
    {
        if (SystemParameters.HighContrast) return "High contrast is enabled";
        if (System.Windows.Forms.SystemInformation.TerminalServerSession) return "Remote Desktop compatibility mode";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("EnableTransparency") is int enabled && enabled == 0)
                return "Windows transparency effects are disabled";
        }
        catch (System.Security.SecurityException) { return "Windows appearance settings are restricted"; }
        catch (UnauthorizedAccessException) { return "Windows appearance settings are restricted"; }
        return null;
    }
}
