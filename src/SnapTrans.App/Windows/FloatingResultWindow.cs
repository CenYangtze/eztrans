using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using SnapTrans.App.Controls;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Settings;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Windows;

public sealed record RetranslationRequest(string ProfileId, string TargetLanguage);

public sealed class FloatingResultWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IClipboardService _clipboard;
    private readonly Action _save;
    private readonly TextBox _text;
    private readonly TextBlock _status;
    private readonly ComboBox _profilePicker;
    private readonly SearchPicker _language;
    private readonly Grid _body;
    private readonly TextBox _sourcePane;
    private readonly GridSplitter _splitter;
    private readonly Button _retry, _pin, _originalButton, _comparisonButton, _aiButton;
    private readonly DispatcherTimer _streamTimer;
    private readonly StringBuilder _pending = new();
    private string _translation = "";
    private string? _original;
    private bool _showOriginal, _pinned, _manualPlacement, _positioning, _closed, _updatingContext, _sideBySide;
    private MonitorInfo? _monitor;
    private PixelRect _region;
    public bool Pinned => _pinned;
    public string SelectedProfileId { get; private set; } = "";
    public string SelectedTargetLanguage { get; private set; } = "";
    public event EventHandler? RetryRequested;
    public event EventHandler? ImageRequested;
    public event EventHandler? AiImageRequested;
    public event EventHandler? PinnedChanged;
    public event EventHandler<RetranslationRequest>? RetranslationRequested;

    public FloatingResultWindow(AppSettings settings, IClipboardService clipboard, Action save)
    {
        _settings = settings; _clipboard = clipboard; _save = save;
        SystemTheme.Attach(this);
        SetResourceReference(BackgroundProperty, "Brush.Card");
        Title = "屏译 SnapTrans";
        Width = settings.Floating.Width ?? 440;
        Height = settings.Floating.Height ?? 260;
        MinWidth = 360;
        MinHeight = 140;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = false;
        FontSize = 13;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8) });
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: drag anywhere on empty space; model and language apply to this window only.
        var header = new DockPanel { Margin = new Thickness(10, 8, 6, 4), Background = Brushes.Transparent, LastChildFill = true };
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) { _manualPlacement = true; DragMove(); } };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _originalButton = TextTool(tools, "原文", "查看识别原文", (_, _) => ToggleOriginal());
        _originalButton.Visibility = Visibility.Collapsed;
        _comparisonButton = TextTool(tools, "对照", "原文与译文并排", (_, _) => SetComparisonMode(!_sideBySide));
        _comparisonButton.Visibility = Visibility.Collapsed;
        _pin = IconTool(tools, Ui.Glyph.Pin, "钉住（Ctrl+P）", (_, _) => TogglePin());
        IconTool(tools, Ui.Glyph.Copy, "复制译文（Ctrl+C）", async (_, _) => await CopyAsync());
        IconTool(tools, Ui.Glyph.Close, "关闭（Esc）", (_, _) => Close());
        DockPanel.SetDock(tools, Dock.Right); header.Children.Add(tools);
        var context = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _profilePicker = new ComboBox { DisplayMemberPath = nameof(Profile.Name), SelectedValuePath = nameof(Profile.Id), MinWidth = 80, MaxWidth = 124, ToolTip = "本窗口使用的模型" };
        Compact(_profilePicker);
        context.Children.Add(_profilePicker);
        var arrow = Ui.Icon("\uE72A", 11, "Brush.TextSubtle");
        arrow.Margin = new Thickness(7, 0, 7, 0);
        context.Children.Add(arrow);
        _language = new SearchPicker(SearchPicker.Languages(settings.Translation.TargetLanguage), settings.Translation.TargetLanguage) { Width = 110 };
        _language.MakeCompact();
        context.Children.Add(_language);
        header.Children.Add(context);
        _profilePicker.SelectionChanged += (_, _) => ContextChanged();
        _language.ValueChanged += _ => ContextChanged();
        grid.Children.Add(header);

        _text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(16, 8, 16, 12), FontSize = Math.Clamp(settings.Floating.FontSize, 10, 28), IsReadOnlyCaretVisible = true };
        _text.SetResourceReference(StyleProperty, "TextBox.Reader");
        _sourcePane = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 8, 16, 12), FontSize = _text.FontSize, Visibility = Visibility.Collapsed, ToolTip = "识别原文" };
        _sourcePane.SetResourceReference(StyleProperty, "TextBox.Reader");
        _sourcePane.SetResourceReference(ForegroundProperty, "Brush.TextMuted");
        _body = new Grid();
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _splitter = new GridSplitter { Width = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
        Grid.SetColumn(_splitter, 1); Grid.SetColumn(_text, 2);
        _body.Children.Add(_sourcePane); _body.Children.Add(_splitter); _body.Children.Add(_text);
        Grid.SetRow(_body, 1); grid.Children.Add(_body);

        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 5, 6, 5) };
        footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        var footerRow = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _retry = TextTool(actions, "重试", "重新翻译（Ctrl+R）", (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty));
        _retry.Visibility = Visibility.Collapsed;
        TextTool(actions, "译图", "生成原位译图（Ctrl+I）", (_, _) => ImageRequested?.Invoke(this, EventArgs.Empty));
        _aiButton = TextTool(actions, "AI 重绘", "由图像模型重新绘制，可能改动非文字内容", (_, _) => AiImageRequested?.Invoke(this, EventArgs.Empty));
        DockPanel.SetDock(actions, Dock.Right); footerRow.Children.Add(actions);
        _status = Ui.Muted("准备翻译");
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        footerRow.Children.Add(_status);
        footer.Child = footerRow;
        Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var frame = new Border { Child = grid, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        Content = frame;
        _streamTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _streamTimer.Tick += (_, _) => FlushPending();
        SourceInitialized += (_, _) => { WindowPlacement.EnableRoundedCorners(this); WindowPlacement.SetCaptureExcluded(this, settings.General.ExcludeOwnWindowsFromCapture); };
        Loaded += (_, _) => Position();
        PreviewKeyDown += OnShortcut;
        PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            _text.FontSize = Math.Clamp(_text.FontSize + Math.Sign(e.Delta), 10, 28);
            _sourcePane.FontSize = _text.FontSize;
            settings.Floating.FontSize = _text.FontSize; _save(); e.Handled = true;
        };
        SizeChanged += (_, _) =>
        {
            if (_positioning || !IsLoaded) return;
            _manualPlacement = true;
            settings.Floating.Width = ActualWidth;
            settings.Floating.Height = ActualHeight;
            _save();
        };
        Deactivated += (_, _) => { if (settings.Floating.CloseOnDeactivate && !_pinned && IsVisible) Close(); };
        Closed += (_, _) => { _closed = true; _streamTimer.Stop(); _pending.Clear(); };
        SetTranslationContext(settings.Translation.ActiveProfileId, settings.Translation.TargetLanguage);
    }
    private static Button IconTool(Panel panel, string glyph, string tooltip, RoutedEventHandler click)
    {
        var button = Ui.IconButton(glyph, tooltip);
        button.Width = button.Height = button.MinHeight = 30;
        button.FontSize = 13;
        button.Margin = new Thickness(1, 0, 0, 0);
        button.Click += click; panel.Children.Add(button); return button;
    }
    private static Button TextTool(Panel panel, string text, string tooltip, RoutedEventHandler click)
    {
        var button = Ui.Button(text, "Button.Subtle");
        button.ToolTip = tooltip;
        button.FontSize = 12;
        button.MinHeight = button.Height = 28;
        button.Padding = new Thickness(9, 0, 9, 0);
        button.Margin = new Thickness(1, 0, 0, 0);
        button.Click += click; panel.Children.Add(button); return button;
    }
    private static void Compact(ComboBox combo)
    {
        combo.MinHeight = combo.Height = 28;
        combo.FontSize = 12;
        combo.Padding = new Thickness(8, 0, 26, 0);
    }
    public void SetTranslationContext(string profileId, string targetLanguage)
    {
        _updatingContext = true;
        try
        {
            SelectedProfileId = profileId; SelectedTargetLanguage = targetLanguage;
            _profilePicker.ItemsSource = _settings.Profiles.ToArray();
            _profilePicker.SelectedValue = profileId;
            _language.SetItems(SearchPicker.Languages(targetLanguage));
            _language.Value = targetLanguage;
            // AI redraw is offered only while a profile that can generate images is selected for it.
            _aiButton.Visibility = ImageGeneration.IsAvailable(_settings) ? Visibility.Visible : Visibility.Collapsed;
            ApplyFont();
        }
        finally { _updatingContext = false; }
    }
    private void ApplyFont()
    {
        if (string.IsNullOrWhiteSpace(_settings.Floating.FontFamily)) { _text.ClearValue(FontFamilyProperty); _sourcePane.ClearValue(FontFamilyProperty); return; }
        var family = new FontFamily(_settings.Floating.FontFamily + ", Microsoft YaHei UI, Segoe UI");
        _text.FontFamily = family; _sourcePane.FontFamily = family;
    }
    private void ContextChanged()
    {
        if (_updatingContext || _closed || _profilePicker.SelectedItem is not Profile profile) return;
        string target = (_language.Value ?? "").Trim();
        if (target.Length == 0) return;
        if (profile.Id == SelectedProfileId && target == SelectedTargetLanguage) return;
        SelectedProfileId = profile.Id; SelectedTargetLanguage = target;
        RetranslationRequested?.Invoke(this, new(profile.Id, target));
    }
    public void SetRegion(PixelRect region, MonitorInfo monitor)
    {
        _region = region; _monitor = monitor; _manualPlacement = false;
        _positioning = true;
        try
        {
            MaxHeight = monitor.WorkArea.Height / monitor.Scale;
            MaxWidth = monitor.WorkArea.Width / monitor.Scale;
            MinWidth = Math.Min(360, MaxWidth); MinHeight = Math.Min(140, MaxHeight);
            Width = Math.Clamp(_settings.Floating.Width ?? Math.Clamp(region.Width / monitor.Scale, 360, 640), MinWidth, MaxWidth);
            Height = Math.Clamp(_settings.Floating.Height ?? 260, MinHeight, Math.Max(MinHeight, MaxHeight * 0.6));
            Position();
        }
        finally { _positioning = false; }
    }
    private void Position()
    {
        if (_monitor is null || _manualPlacement) return;
        var bounds = PopupPlacement.Compute(_region, new PixelSize((int)Math.Round(Width * _monitor.Scale), (int)Math.Round(Height * _monitor.Scale)), _monitor.WorkArea, (int)Math.Round(8 * _monitor.Scale));
        WindowPlacement.SetBounds(this, bounds);
    }
    public void SetContent(string text)
    {
        CancelStreaming(); _translation = text == "[NO_TEXT]" ? "未识别到文字" : text;
        if (!_showOriginal) _text.Text = _translation;
    }
    public void SetOriginal(string? text)
    {
        _original = text;
        _showOriginal = false;
        _originalButton.Content = "原文";
        _sourcePane.Text = text ?? "";
        _sideBySide = text is not null && _settings.Floating.SideBySide;
        ApplyComparisonLayout();
        _text.Text = _translation;
    }
    public void SetComparisonMode(bool enabled)
    {
        _settings.Floating.SideBySide = enabled;
        _sideBySide = enabled && _original is not null;
        if (_sideBySide) { _showOriginal = false; _text.Text = _translation; }
        ApplyComparisonLayout();
        _save();
    }
    private void ApplyComparisonLayout()
    {
        _sourcePane.Visibility = _sideBySide ? Visibility.Visible : Visibility.Collapsed;
        _splitter.Visibility = _sideBySide ? Visibility.Visible : Visibility.Collapsed;
        _body.ColumnDefinitions[0].Width = _sideBySide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _body.ColumnDefinitions[1].Width = _sideBySide ? GridLength.Auto : new GridLength(0);
        _body.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        _originalButton.Visibility = _original is not null && !_sideBySide ? Visibility.Visible : Visibility.Collapsed;
        _comparisonButton.Visibility = _original is null ? Visibility.Collapsed : Visibility.Visible;
        _comparisonButton.Content = _sideBySide ? "单栏" : "对照";
    }
    public void AppendText(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendText(text)); return; }
        if (_closed) return;
        _pending.Append(text);
        if (!_streamTimer.IsEnabled) _streamTimer.Start();
    }
    private void FlushPending()
    {
        if (_pending.Length == 0) return;
        string append = _pending.ToString(); _pending.Clear(); _translation += append;
        if (!_showOriginal) { _text.AppendText(append); _text.ScrollToEnd(); }
        GrowToContent();
    }
    private void GrowToContent()
    {
        if (_monitor is null || _manualPlacement || _settings.Floating.Height is not null || _translation.Length == 0) return;
        var formatted = new FormattedText(_translation, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch),
            _text.FontSize, Foreground, _monitor.Scale) { MaxTextWidth = Math.Max(40, (_sideBySide ? ActualWidth / 2 : ActualWidth) - 36) };
        double height = Math.Clamp(formatted.Height + 124, 140, Math.Max(140, _monitor.WorkArea.Height / _monitor.Scale * 0.6));
        if (height <= Height + 1) return;
        _positioning = true;
        try { Height = height; Position(); }
        finally { _positioning = false; }
    }
    public void FinishStreaming()
    {
        FlushPending(); _streamTimer.Stop();
        if (_translation.Trim() == "[NO_TEXT]") { _translation = "未识别到文字"; if (!_showOriginal) _text.Text = _translation; }
    }
    public void CancelStreaming() { _streamTimer.Stop(); _pending.Clear(); }
    public void SetStatus(string status, bool isError = false)
    {
        _status.Text = status;
        _status.ToolTip = status;
        Ui.Paint(_status, isError ? "Brush.Danger" : "Brush.TextMuted");
        _retry.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
    }
    private void TogglePin()
    {
        _pinned = !_pinned;
        _pin.Content = _pinned ? "\uE840" : Ui.Glyph.Pin;
        _pin.ToolTip = _pinned ? "取消钉住（Ctrl+P）" : "钉住（Ctrl+P）";
        if (_pinned) _pin.SetResourceReference(ForegroundProperty, "Brush.Accent"); else _pin.ClearValue(ForegroundProperty);
        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ToggleOriginal()
    {
        if (_original is null) return;
        FlushPending(); _showOriginal = !_showOriginal;
        _text.Text = _showOriginal ? _original : _translation;
        _originalButton.Content = _showOriginal ? "译文" : "原文";
    }
    private async Task CopyAsync(bool original = false)
    {
        try
        {
            FlushPending();
            await _clipboard.SetTextAsync(original ? _original ?? "" : _sourcePane.IsKeyboardFocusWithin && _sourcePane.SelectionLength > 0 ? _sourcePane.SelectedText : _text.SelectionLength > 0 ? _text.SelectedText : _translation);
            SetStatus("已复制到剪贴板");
        }
        catch { SetStatus("剪贴板被占用，请稍后重试", true); }
    }
    private async void OnShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        switch (e.Key)
        {
            case Key.C: await CopyAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); break;
            case Key.P: TogglePin(); break;
            case Key.I: ImageRequested?.Invoke(this, EventArgs.Empty); break;
            case Key.R: RetryRequested?.Invoke(this, EventArgs.Empty); break;
            default: return;
        }
        e.Handled = true;
    }
}
