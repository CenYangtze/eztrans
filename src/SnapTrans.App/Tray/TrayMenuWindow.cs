using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SnapTrans.App.Controls;
using SnapTrans.Core.Geometry;
using SnapTrans.Platform.Windows;
using Button = System.Windows.Controls.Button;
using Forms = System.Windows.Forms;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace SnapTrans.App.Tray;

/// <summary>One row of the tray menu; a null <see cref="Text"/> is a separator.</summary>
public sealed record TrayMenuItem(string? Text, string? Glyph = null, Action? Invoke = null, string? Shortcut = null, bool Checked = false)
{
    public static TrayMenuItem Separator { get; } = new((string?)null);
}

/// <summary>
/// The tray icon's context menu, drawn with the application theme instead of the Win32 menu look.
/// It closes when it loses focus, on Esc, or after a command runs.
/// </summary>
public sealed class TrayMenuWindow : Window
{
    private const double ShadowMargin = 12;
    private readonly PixelPoint _anchor;
    private bool _closing;

    public TrayMenuWindow(IReadOnlyList<TrayMenuItem> items, PixelPoint anchor)
    {
        _anchor = anchor;
        Title = "屏译菜单";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = 13;
        Opacity = 0;
        SystemTheme.Initialize();
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        // Start near the cursor so the window is created on the right monitor; Place() then aligns it in pixels.
        double scale = Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleX);
        Left = anchor.X / scale;
        Top = anchor.Y / scale - 200;

        var panel = new StackPanel { MinWidth = 220 };
        KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Cycle);
        foreach (var item in items) panel.Children.Add(item.Text is null ? SeparatorLine() : Row(item));
        var surface = new Border
        {
            Child = panel, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(5),
            Margin = new Thickness(ShadowMargin), Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = .26 }
        };
        surface.SetResourceReference(Border.BackgroundProperty, "Brush.Popup");
        surface.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        Content = surface;

        Loaded += (_, _) => { Place(); Opacity = 1; Activate(); panel.Children.OfType<Button>().FirstOrDefault()?.Focus(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        // Close() must not run again from Deactivated while the window is already closing.
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => CloseOnce();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseOnce(); e.Handled = true; } };
    }

    public void Dismiss() => CloseOnce();

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>Puts the menu's lower-right corner at the cursor, flipped or shifted to stay inside the work area.</summary>
    private void Place()
    {
        try
        {
            var size = WindowPlacement.GetBounds(this);
            var work = Forms.Screen.FromPoint(new System.Drawing.Point(_anchor.X, _anchor.Y)).WorkingArea;
            int margin = (int)Math.Round(ShadowMargin * VisualTreeHelper.GetDpi(this).DpiScaleX);
            int x = _anchor.X - size.Width + margin, y = _anchor.Y - size.Height + margin;
            if (x < work.Left - margin) x = _anchor.X - margin;
            if (y < work.Top - margin) y = _anchor.Y - margin;
            x = Math.Clamp(x, work.Left - margin, Math.Max(work.Left - margin, work.Right - size.Width + margin));
            y = Math.Clamp(y, work.Top - margin, Math.Max(work.Top - margin, work.Bottom - size.Height + margin));
            WindowPlacement.SetBounds(this, new PixelRect(x, y, size.Width, size.Height));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { }
    }

    private Button Row(TrayMenuItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Ui.Icon(item.Checked ? Ui.Glyph.Check : item.Glyph ?? "", 14, item.Checked ? "Brush.Accent" : "Brush.TextMuted");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(icon);
        var text = new TextBlock { Text = item.Text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (!string.IsNullOrWhiteSpace(item.Shortcut))
        {
            var shortcut = Ui.Muted(item.Shortcut);
            shortcut.Margin = new Thickness(28, 0, 0, 0);
            shortcut.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(shortcut, 2);
            grid.Children.Add(shortcut);
        }
        var button = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.SetResourceReference(StyleProperty, "Button.Menu");
        button.Click += (_, _) =>
        {
            CloseOnce();
            if (item.Invoke is not { } action) return;
            // Give the compositor a moment to remove the menu so a capture never includes it.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            timer.Tick += (_, _) => { timer.Stop(); action(); };
            timer.Start();
        };
        return button;
    }

    private static Border SeparatorLine()
    {
        var line = new Border { Height = 1, Margin = new Thickness(6, 5, 6, 5) };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
        return line;
    }
}
