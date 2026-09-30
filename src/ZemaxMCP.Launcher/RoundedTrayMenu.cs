using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZemaxMCP.Launcher;

internal sealed class RoundedTrayMenu : ContextMenuStrip
{
    internal static GraphicsPath Outline(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) return path;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateShape();
    }

    protected override void OnOpened(EventArgs e)
    {
        UpdateShape();
        base.OnOpened(e);
    }

    private void UpdateShape()
    {
        if (Width < 2 || Height < 2) return;
        var old = Region;
        using var path = Outline(new Rectangle(0, 0, Width, Height), CornerRadius);
        Region = new Region(path);
        old?.Dispose();
    }

    internal int CornerRadius => Math.Max(8, (int)Math.Round(12.0 * DeviceDpi / 96));
}
