using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using SystemColors = System.Windows.SystemColors;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace ZemaxMCP.Launcher;

// Owned, keyboard-accessible confirmations; never include credentials or mutate client files here.
internal sealed class LauncherDialog : Window
{
    internal LauncherDialog(Window owner, string title, string message, string confirm,
        IEnumerable<(string Name, string Status)>? rows = null)
    {
        Owner = owner;
        Title = title;
        Icon = owner.Icon;
        Width = 480;
        MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height - 48);
        MaxWidth = Math.Max(280, SystemParameters.WorkArea.Width - 48);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = owner.FontFamily;
        FontSize = 13;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(owner.Resources);
        SourceInitialized += (_, _) => WindowMaterial.Apply(this, "mica");

        var layout = new DockPanel { Margin = new Thickness(24) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Not now", IsCancel = true, MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0), Style = owner.TryFindResource("ActionButton") as Style };
        var accept = new Button { Content = confirm, IsDefault = true, MinWidth = 108,
            Style = owner.TryFindResource("PrimaryButton") as Style };
        accept.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = SystemColors.WindowTextBrush, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 10, 0, 14),
            Foreground = SystemColors.WindowTextBrush, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        if (rows != null)
        {
            foreach (var row in rows)
            {
                var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(new TextBlock { Text = row.Name, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 12, 0), Foreground = SystemColors.WindowTextBrush });
                var state = new TextBlock { Text = row.Status, Foreground = SystemParameters.HighContrast
                    ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(75, 100, 132)) };
                Grid.SetColumn(state, 1);
                grid.Children.Add(state);
                body.Children.Add(grid);
            }
        }
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(120, MaxHeight - 150) });
        Content = new Border { Margin = new Thickness(16), CornerRadius = new CornerRadius(16),
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush
                : owner.TryFindResource("GlassSurface") as Brush ?? SystemColors.WindowBrush,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush
                : owner.TryFindResource("GlassEdge") as Brush,
            BorderThickness = new Thickness(1), Child = layout };
    }

    internal static bool Confirm(Window owner, string title, string message, string confirm,
        IEnumerable<(string Name, string Status)>? rows = null) =>
        new LauncherDialog(owner, title, message, confirm, rows).ShowDialog() == true;
}
