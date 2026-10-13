using System.Windows.Controls;
using System.Windows.Input;

namespace ZemaxMCP.Launcher;

internal static class NestedScrollWheel
{
    // Keep a long activity list usable, but never trap the page wheel at its edges.
    internal static void Route(ScrollViewer inner, ScrollViewer outer, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        if (inner.ScrollableHeight > 0 &&
            (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
            return;
        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta / 120.0 * 48);
        e.Handled = true;
    }
}
