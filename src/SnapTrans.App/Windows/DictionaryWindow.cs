using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SnapTrans.App.Controls;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Dictionaries;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Settings;
using SnapTrans.Platform.Windows;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTrans.App.Windows;

/// <summary>
/// The dictionary card: headword, phonetics, meanings by part of speech, forms, phrases and examples.
/// It opens under the word it explains and grows with its content; the search box looks up another word.
/// </summary>
public sealed class DictionaryWindow : Window
{
    private const double ShadowMargin = 12, CardWidth = 400;
    private readonly AppSettings _settings;
    private readonly IClipboardService _clipboard;
    private readonly TextBox _search = new() { Tag = "输入单词，按 Enter 查询", MinHeight = 30, Height = 30, FontSize = 13 };
    private readonly StackPanel _content = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = Ui.Muted("");
    private readonly Button _pin, _web;
    private DictionaryEntry? _entry;
    private string _query = "";
    private PixelRect _anchor;
    private MonitorInfo? _monitor;
    private bool _pinned, _manualPlacement, _placed;

    public bool Pinned => _pinned;
    public bool IsClosed { get; private set; }
    /// <summary>Raised when the user asks for a word from the search box or retries a failed lookup.</summary>
    public event Action<string>? LookupRequested;

    public DictionaryWindow(AppSettings settings, IClipboardService clipboard)
    {
        _settings = settings; _clipboard = clipboard;
        Title = "屏译词典";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = CardWidth + ShadowMargin * 2;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = 13;
        Opacity = 0;
        SystemTheme.Initialize();
        SetResourceReference(FontFamilyProperty, "Font.Ui");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: the search box, then pin, copy and close.
        var header = new DockPanel { Margin = new Thickness(12, 10, 6, 6), LastChildFill = true };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _pin = Tool(tools, Ui.Glyph.Pin, "钉住（Ctrl+P）", (_, _) => TogglePin());
        Tool(tools, Ui.Glyph.Copy, "复制释义（Ctrl+C）", async (_, _) => await CopyAsync());
        Tool(tools, Ui.Glyph.Close, "关闭（Esc）", (_, _) => Close());
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (WordPicker.Normalize(_search.Text) is { } word) LookupRequested?.Invoke(word);
        };
        header.Children.Add(_search);
        layout.Children.Add(header);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = _content };
        _content.Margin = new Thickness(16, 6, 16, 14);
        Grid.SetRow(_scroll, 1);
        layout.Children.Add(_scroll);

        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 4, 6, 4) };
        footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        var footerRow = new DockPanel();
        _web = Ui.Button("网页版", "Button.Subtle");
        _web.ToolTip = "在浏览器中查看完整释义";
        _web.FontSize = 12;
        _web.MinHeight = _web.Height = 28;
        _web.Padding = new Thickness(9, 0, 9, 0);
        _web.Click += (_, _) => OpenWebPage();
        DockPanel.SetDock(_web, Dock.Right);
        footerRow.Children.Add(_web);
        _status.VerticalAlignment = VerticalAlignment.Center;
        footerRow.Children.Add(_status);
        footer.Child = footerRow;
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        // The shadow sits on its own layer: an effect on the card itself would rasterize and soften all its text.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(10), Margin = new Thickness(ShadowMargin),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = .24 }
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        var surface = new Border { Child = layout, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(ShadowMargin) };
        surface.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        surface.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        // Buttons and the search box handle their own clicks; anywhere else drags the card.
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 1 || e.ButtonState != MouseButtonState.Pressed) return;
            _manualPlacement = true;
            DragMove();
        };
        var root = new Grid();
        root.Children.Add(shadow);
        root.Children.Add(surface);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            // Layered windows cannot always be excluded from capture; the card still works without it.
            try { WindowPlacement.SetCaptureExcluded(this, settings.General.ExcludeOwnWindowsFromCapture); }
            catch (System.ComponentModel.Win32Exception) { }
        };
        Loaded += (_, _) => { Place(); Opacity = 1; _placed = true; };
        SizeChanged += (_, _) => { if (_placed) Place(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Deactivated += (_, _) => { if (settings.Floating.CloseOnDeactivate && !_pinned && IsVisible && !IsClosed) Close(); };
        Closing += (_, _) => IsClosed = true;
        PreviewKeyDown += OnShortcut;
    }

    /// <summary>Places the card under (or above) the word; coordinates are virtual-screen pixels.</summary>
    public void SetAnchor(PixelRect anchor, MonitorInfo monitor)
    {
        _anchor = anchor; _monitor = monitor; _manualPlacement = false;
        MaxHeight = Math.Max(240, monitor.WorkArea.Height / monitor.Scale * 0.7) + ShadowMargin * 2;
        if (_placed) Place();
        else
        {
            // Start on the target monitor so the first layout already uses its DPI; Place() aligns in pixels.
            Left = anchor.Left / monitor.Scale;
            Top = anchor.Bottom / monitor.Scale;
        }
    }

    public void ShowLoading(string query)
    {
        _query = query; _entry = null;
        _search.Text = query;
        _content.Children.Clear();
        _content.Children.Add(Headword(query));
        var waiting = Ui.Muted("查询中…", 13);
        waiting.Margin = new Thickness(0, 8, 0, 4);
        _content.Children.Add(waiting);
        _status.Text = "";
        _scroll.ScrollToTop();
    }

    public void ShowEntry(DictionaryEntry entry)
    {
        _entry = entry; _query = entry.Word;
        _content.Children.Clear();
        ApplyFont();
        double size = Math.Clamp(_settings.Floating.FontSize, 10, 28);
        _content.Children.Add(Headword(entry.Word));

        var phonetics = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (entry.PhoneticUk is not null && entry.PhoneticUk == entry.PhoneticUs) phonetics.Children.Add(Phonetic("", entry.PhoneticUk));
        else
        {
            if (entry.PhoneticUk is not null) phonetics.Children.Add(Phonetic("英", entry.PhoneticUk));
            if (entry.PhoneticUs is not null) phonetics.Children.Add(Phonetic("美", entry.PhoneticUs));
        }
        if (phonetics.Children.Count > 0) _content.Children.Add(phonetics);

        if (entry.Tags.Count > 0)
        {
            var tags = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (string tag in entry.Tags) tags.Children.Add(Chip(tag));
            _content.Children.Add(tags);
        }

        var senses = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        senses.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        senses.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < entry.Senses.Count; row++)
        {
            senses.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sense = entry.Senses[row];
            if (sense.PartOfSpeech is not null)
            {
                var label = Ui.Text(sense.PartOfSpeech, size - 1, FontWeights.SemiBold, "Brush.Accent", wrap: false);
                label.FontStyle = FontStyles.Italic;
                label.Margin = new Thickness(0, 0, 10, 7);
                Grid.SetRow(label, row);
                senses.Children.Add(label);
            }
            var meaning = Ui.Text(sense.Meaning, size);
            meaning.LineHeight = Math.Round(size * 1.5);
            meaning.Margin = new Thickness(0, 0, 0, 7);
            Grid.SetRow(meaning, row);
            Grid.SetColumn(meaning, sense.PartOfSpeech is null ? 0 : 1);
            if (sense.PartOfSpeech is null) Grid.SetColumnSpan(meaning, 2);
            senses.Children.Add(meaning);
        }
        _content.Children.Add(senses);

        if (entry.Forms.Count > 0)
        {
            // One block per form, so a line never breaks between a label and its word.
            var forms = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var form in entry.Forms)
            {
                var item = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 0, 14, 3) };
                item.Inlines.Add(Colored(form.Name + " ", "Brush.TextSubtle"));
                item.Inlines.Add(Colored(form.Value, "Brush.TextMuted"));
                forms.Children.Add(item);
            }
            _content.Children.Add(forms);
        }

        if (entry.Phrases.Count > 0)
        {
            _content.Children.Add(Section("词组"));
            foreach (var phrase in entry.Phrases)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 4), LineHeight = 19 };
                line.Inlines.Add(new Run(phrase.Text) { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(Colored("  " + phrase.Meaning, "Brush.TextMuted"));
                _content.Children.Add(line);
            }
        }

        if (entry.Examples.Count > 0)
        {
            _content.Children.Add(Section("例句"));
            foreach (var example in entry.Examples)
            {
                var text = Ui.Text(example.Text, 13);
                text.LineHeight = 19;
                _content.Children.Add(text);
                var translation = Ui.Muted(example.Translation, 12.5);
                translation.Margin = new Thickness(0, 1, 0, 7);
                _content.Children.Add(translation);
            }
        }
        _status.Text = entry.SourceName;
        _scroll.ScrollToTop();
    }

    public void ShowError(string query, string message)
    {
        _query = query; _entry = null;
        _content.Children.Clear();
        _content.Children.Add(Headword(query));
        var text = Ui.Muted(message, 13);
        text.Margin = new Thickness(0, 8, 0, 10);
        _content.Children.Add(text);
        var retry = Ui.Button("重试", glyph: Ui.Glyph.Refresh);
        retry.Click += (_, _) => LookupRequested?.Invoke(_query);
        _content.Children.Add(retry);
        _status.Text = "";
    }

    /// <summary>Selects the query so typing replaces it; called after the card is shown.</summary>
    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    private void Place()
    {
        if (_monitor is null || _manualPlacement) return;
        try
        {
            var bounds = WindowPlacement.GetBounds(this);
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int margin = (int)Math.Round(ShadowMargin * scale), gap = (int)Math.Round(6 * scale);
            int width = bounds.Width - margin * 2, height = bounds.Height - margin * 2;
            var work = _monitor.WorkArea;
            int below = _anchor.Bottom + gap, above = _anchor.Top - gap - height;
            // Under the word when it fits; otherwise above it; otherwise as low as the work area allows.
            int y = below + height <= work.Bottom || above < work.Top ? below : above;
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
            int x = Math.Clamp(_anchor.Left, work.Left, Math.Max(work.Left, work.Right - width));
            WindowPlacement.SetPosition(this, new PixelPoint(x - margin, y - margin));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { }
    }

    private void ApplyFont()
    {
        if (string.IsNullOrWhiteSpace(_settings.Floating.FontFamily)) _content.ClearValue(TextElement.FontFamilyProperty);
        else TextElement.SetFontFamily(_content, new FontFamily(_settings.Floating.FontFamily + ", Microsoft YaHei UI, Segoe UI"));
    }

    private static TextBlock Headword(string text)
    {
        var block = Ui.Text(text, 22, FontWeights.SemiBold);
        block.LineHeight = 28;
        return block;
    }

    private static TextBlock Phonetic(string label, string value)
    {
        var block = new TextBlock { FontSize = 13, Margin = new Thickness(0, 0, 16, 0) };
        if (label.Length > 0) block.Inlines.Add(Colored(label + " ", "Brush.TextSubtle"));
        block.Inlines.Add(Colored("/" + value.Trim('/', '[', ']', ' ') + "/", "Brush.TextMuted"));
        // Segoe UI covers IPA; the CJK interface font does not draw stress marks well.
        block.FontFamily = new FontFamily("Segoe UI, Arial, Microsoft YaHei UI");
        return block;
    }

    private static Border Chip(string text)
    {
        var chip = new Border { Child = Ui.Text(text, 11, brush: "Brush.TextMuted", wrap: false), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 6, 4) };
        chip.SetResourceReference(Border.BackgroundProperty, "Brush.Control");
        return chip;
    }

    private static TextBlock Section(string title)
    {
        var block = Ui.Text(title, 12, FontWeights.SemiBold, "Brush.TextSubtle");
        block.Margin = new Thickness(0, 12, 0, 5);
        return block;
    }

    private static Run Colored(string text, string brush)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, brush);
        return run;
    }

    private static Button Tool(Panel panel, string glyph, string tooltip, RoutedEventHandler click)
    {
        var button = Ui.IconButton(glyph, tooltip);
        button.Width = button.Height = button.MinHeight = 30;
        button.FontSize = 13;
        button.Margin = new Thickness(1, 0, 0, 0);
        button.Click += click;
        panel.Children.Add(button);
        return button;
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        _pin.Content = _pinned ? "" : Ui.Glyph.Pin;
        _pin.ToolTip = _pinned ? "取消钉住（Ctrl+P）" : "钉住（Ctrl+P）";
        if (_pinned) _pin.SetResourceReference(ForegroundProperty, "Brush.Accent"); else _pin.ClearValue(ForegroundProperty);
    }

    private string Summary()
    {
        if (_entry is null) return _query;
        var text = new StringBuilder(_entry.Word);
        if (_entry.PhoneticUk is not null || _entry.PhoneticUs is not null)
            text.Append("  /").Append((_entry.PhoneticUs ?? _entry.PhoneticUk)!.Trim('/', ' ')).Append('/');
        foreach (var sense in _entry.Senses) text.AppendLine().Append(sense.PartOfSpeech is null ? "" : sense.PartOfSpeech + " ").Append(sense.Meaning);
        return text.ToString();
    }

    private async Task CopyAsync()
    {
        try { await _clipboard.SetTextAsync(Summary()); _status.Text = "已复制"; }
        catch { _status.Text = "剪贴板被占用，请稍后重试"; }
    }

    private void OpenWebPage()
    {
        if (string.IsNullOrWhiteSpace(_query)) return;
        string source = _entry?.SourceId ?? (_settings.Dictionary.Source == DictionarySources.Google ? DictionarySources.Google : DictionarySources.Youdao);
        try { Process.Start(new ProcessStartInfo(DictionarySources.WebPage(source, _query).AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = "无法打开浏览器"; }
    }

    private async void OnShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.P) { TogglePin(); e.Handled = true; }
        // Ctrl+C keeps its normal meaning while part of the search text is selected.
        else if (e.Key == Key.C && !(_search.IsKeyboardFocusWithin && _search.SelectionLength > 0 && _search.SelectionLength < _search.Text.Length))
        { e.Handled = true; await CopyAsync(); }
    }
}
