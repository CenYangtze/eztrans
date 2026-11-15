using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace SnapTrans.App.Controls;

/// <summary>Small factory helpers so code-built windows share the App.xaml design language.</summary>
public static class Ui
{
    public static class Glyph
    {
        public const string Settings = "";
        public const string Keyboard = "";
        public const string Globe = "";
        public const string Robot = "";
        public const string Font = "";
        public const string Picture = "";
        public const string Repair = "";
        public const string Copy = "";
        public const string Pin = "";
        public const string Unpin = "";
        public const string Close = "";
        public const string Refresh = "";
        public const string Save = "";
        public const string Search = "";
        public const string View = "";
        public const string Delete = "";
        public const string Add = "";
        public const string Folder = "";
        public const string Info = "";
        public const string Shield = "";
        public const string Check = "";
        public const string Personalize = "\uE771";
        public const string ChevronDown = "\uE70D";
        public const string Repeat = "\uE8EE";
        public const string Pause = "\uE769";
        public const string Exit = "\uE7E8";
        public const string Download = "\uE896";
        public const string Grid = "\uE8A9";
        public const string Dictionary = "\uE82D";
    }

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string brush = "Brush.Text", bool wrap = true)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (!wrap) block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public static TextBlock Muted(string text, double size = 12) => Text(text, size, brush: "Brush.TextMuted");

    public static TextBlock Icon(string glyph, double size = 16, string brush = "Brush.Text")
    {
        var block = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public static void Paint(TextBlock block, string brush) => block.SetResourceReference(TextBlock.ForegroundProperty, brush);

    public static Button Button(string text, string? style = null, string? glyph = null)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        if (style is not null) button.SetResourceReference(FrameworkElement.StyleProperty, style);
        button.Content = glyph is null ? text : text.Length == 0 ? IconOnly(glyph) : WithIcon(glyph, text);
        return button;
    }

    public static Button IconButton(string glyph, string tooltip)
    {
        var button = new Button { Content = glyph, ToolTip = tooltip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.Icon");
        return button;
    }

    private static TextBlock IconOnly(string glyph)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        return icon;
    }

    public static StackPanel WithIcon(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            if (child is FrameworkElement element && element.Margin == default) element.Margin = new Thickness(0, 0, 8, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    /// <summary>A rounded surface used for setting tiles and cards.</summary>
    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var border = new Border { Child = child, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(16, 12, 16, 12) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        return border;
    }

    /// <summary>A key-cap chip such as "Ctrl" in a shortcut list.</summary>
    public static Border Key(string text)
    {
        var label = Text(text, 12, FontWeights.SemiBold, wrap: false);
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        var border = new Border { Child = label, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Control");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.ControlBorder");
        return border;
    }

    private static ImageSource? _logo;

    /// <summary>The application icon (the same artwork as the EXE and tray icon).</summary>
    public static ImageSource LogoSource
    {
        get
        {
            if (_logo is not null) return _logo;
            using var stream = typeof(Ui).Assembly.GetManifestResourceStream("SnapTrans.Icon")
                ?? throw new InvalidOperationException("未找到应用图标资源。");
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return _logo = frame;
        }
    }

    public static System.Windows.Controls.Image Logo(double size)
    {
        var image = new System.Windows.Controls.Image { Source = LogoSource, Width = size, Height = size, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    public static Brush Resource(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
}
