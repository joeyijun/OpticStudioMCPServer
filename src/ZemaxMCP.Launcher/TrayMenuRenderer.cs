using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZemaxMCP.Launcher;

// Keep native keyboard navigation and dismissal; only replace the menu's paint treatment.
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public TrayMenuRenderer() { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnRenderToolStripBackground(e); return; }
        using var brush = new LinearGradientBrush(e.AffectedBounds, Color.FromArgb(252, 253, 255), Color.FromArgb(237, 243, 250), 90f);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnRenderMenuItemBackground(e); return; }
        if (!e.Item.Selected) return;
        var rectangle = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var path = new GraphicsPath();
        const int diameter = 12;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        using var fill = new SolidBrush(e.Item.Text == "Exit" ? Color.FromArgb(255, 235, 233) : Color.FromArgb(228, 238, 251));
        var previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(fill, path);
        e.Graphics.SmoothingMode = previous;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (!SystemInformation.HighContrast)
            e.TextColor = e.Item.Text == "Exit" ? Color.FromArgb(186, 48, 40) : Color.FromArgb(29, 29, 31);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnRenderToolStripBorder(e); return; }
        using var pen = new Pen(Color.FromArgb(211, 222, 236));
        using var path = RoundedTrayMenu.Outline(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
            (e.ToolStrip as RoundedTrayMenu)?.CornerRadius ?? 12);
        var previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
        e.Graphics.SmoothingMode = previous;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnRenderSeparator(e); return; }
        using var pen = new Pen(Color.FromArgb(225, 231, 240));
        e.Graphics.DrawLine(pen, 12, e.Item.Height / 2, e.Item.Width - 12, e.Item.Height / 2);
    }
}
