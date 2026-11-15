using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SkiaSharp;
using SnapTrans.App.Controls;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Settings;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Windows;

public sealed class ImageResultWindow : Window
{
    private readonly IClipboardService _clipboard;
    private readonly AppSettings? _settings;
    private readonly Image _image;
    private readonly ImageSource _original;
    private readonly Grid _progress;
    private readonly StackPanel _progressContents;
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(150, 12, 14, 18));
    private readonly TextBlock _status;
    private readonly TextBlock _summary;
    private readonly Popup _toolbar;
    private readonly DispatcherTimer _hideToolbarTimer;
    private SKBitmap? _output;
    private ImageSource? _translated;
    private bool _comparing;
    private bool _processing = true;
    private bool _aiGenerated;
    public event EventHandler? RetryRequested;
    public event EventHandler? AiImageRequested;

    public ImageResultWindow(PixelRect region, SKBitmap original, IClipboardService clipboard, MonitorInfo? monitor = null, AppSettings? settings = null)
    {
        _clipboard = clipboard; _settings = settings;
        _original = BitmapInterop.ToBitmapSource(original);
        Title = "屏译 · 译图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        double scale = monitor?.Scale ?? 1;
        Width = region.Width / scale; Height = region.Height / scale;
        var grid = new Grid { SnapsToDevicePixels = true, UseLayoutRounding = true };
        _image = new Image { Source = _original, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        grid.Children.Add(_image);
        _progress = new Grid { Background = Dim };
        var progressContents = _progressContents = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _status = new TextBlock { Text = "正在识别文字…", Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        progressContents.Children.Add(new Border { Child = _status, Background = new SolidColorBrush(Color.FromArgb(235, 31, 35, 43)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8) });
        var retry = Ui.Button("重试", "Button.Accent");
        retry.Visibility = Visibility.Collapsed;
        retry.HorizontalAlignment = HorizontalAlignment.Center;
        retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        progressContents.Children.Add(retry);
        _retry = retry;
        _progress.Children.Add(progressContents);
        grid.Children.Add(_progress);
        Content = grid;
        var toolPanel = new StackPanel { Orientation = Orientation.Horizontal };
        Button Tool(string glyph, string text, string tooltip, RoutedEventHandler callback)
        {
            var button = Ui.Button(text, "Button.Overlay", glyph);
            button.ToolTip = tooltip;
            button.MinHeight = button.Height = 30;
            button.Margin = new Thickness(1, 0, 1, 0);
            button.Click += callback; toolPanel.Children.Add(button); return button;
        }
        Tool(Ui.Glyph.Save, "保存", "另存为 PNG（Ctrl+S）", async (_, _) => await SaveAsync());
        Tool(Ui.Glyph.Copy, "复制", "复制图片（Ctrl+C）", async (_, _) => await CopyAsync());
        Tool(Ui.Glyph.Refresh, "重试", "重新生成", (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty));
        // AI redraw is offered only while a profile that can generate images is selected for it.
        if (settings is not null && ImageGeneration.IsAvailable(settings))
            Tool("\uE8B3", "AI 重绘", "由图像模型重新绘制，可能改动非文字内容", (_, _) => AiImageRequested?.Invoke(this, EventArgs.Empty));
        var compare = Tool(Ui.Glyph.View, "原图", "按住查看原图（或按住空格）", (_, _) => { });
        compare.PreviewMouseLeftButtonDown += (_, e) => { Compare(true); compare.CaptureMouse(); e.Handled = true; };
        compare.PreviewMouseLeftButtonUp += (_, e) => { Compare(false); compare.ReleaseMouseCapture(); e.Handled = true; };
        compare.LostMouseCapture += (_, _) => Compare(false);
        Tool(Ui.Glyph.Close, "", "关闭（Esc）", (_, _) => Close());
        _summary = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(196, 201, 210)), FontSize = 11.5, Margin = new Thickness(10, 2, 10, 6), MaxWidth = 440, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var toolSurface = new StackPanel();
        toolSurface.Children.Add(toolPanel);
        toolSurface.Children.Add(_summary);
        _toolbar = new Popup { PlacementTarget = grid, Placement = monitor is not null && region.Bottom + 72 * scale > monitor.WorkArea.Bottom ? PlacementMode.Top : PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = true, Child = new Border { Child = toolSurface, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(240, 31, 35, 43)), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 6) } };
        _hideToolbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _hideToolbarTimer.Tick += (_, _) =>
        {
            _hideToolbarTimer.Stop();
            if (!IsMouseOver && !toolSurface.IsMouseOver) _toolbar.IsOpen = false;
        };
        MouseEnter += (_, _) => { _hideToolbarTimer.Stop(); _toolbar.IsOpen = true; };
        MouseLeave += (_, _) => _hideToolbarTimer.Start();
        toolSurface.MouseEnter += (_, _) => _hideToolbarTimer.Stop();
        toolSurface.MouseLeave += (_, _) => _hideToolbarTimer.Start();
        grid.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) { _toolbar.IsOpen = false; DragMove(); } };
        SourceInitialized += (_, _) =>
        {
            WindowPlacement.SetBounds(this, region);
            WindowPlacement.SetCaptureExcluded(this, settings?.General.ExcludeOwnWindowsFromCapture ?? false);
        };
        Loaded += (_, _) => WindowPlacement.SetBounds(this, region);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Space) Compare(true);
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) await CopyAsync();
            else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) await SaveAsync();
            else return;
            e.Handled = true;
        };
        PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) { Compare(false); e.Handled = true; } };
        Deactivated += (_, _) => Compare(false);
        Closed += (_, _) => { _hideToolbarTimer.Stop(); _toolbar.IsOpen = false; _output?.Dispose(); _output = null; };
    }
    private readonly Button _retry;
    public void BeginProcessing()
    {
        _processing = true;
        _progress.Background = Dim;
        _progressContents.VerticalAlignment = VerticalAlignment.Center;
        _progress.Visibility = Visibility.Visible;
        _retry.Visibility = Visibility.Collapsed;
        _summary.Visibility = Visibility.Collapsed;
        _status.Text = "正在识别文字…";
    }
    public void SetStatus(string text, bool isError = false)
    {
        _status.Text = text;
        _progress.Visibility = _processing ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = (_aiGenerated ? "AI 生成，可能与原图不同 · " : "") + text;
        _summary.Visibility = _processing ? Visibility.Collapsed : Visibility.Visible;
        _summary.Foreground = isError ? Brushes.LightSalmon : new SolidColorBrush(Color.FromRgb(196, 201, 210));
        ToolTip = text;
    }
    public void SetAiGenerated(bool aiGenerated)
    {
        _aiGenerated = aiGenerated;
        if (aiGenerated)
        {
            _summary.Text = "AI 生成，可能与原图不同";
            _summary.Visibility = Visibility.Visible;
            _summary.Foreground = Brushes.LightSalmon;
        }
    }
    /// <summary>Shows the blocks translated so far while the remaining requests are still running.</summary>
    public void ShowPreview(ImageSource frame, int completed, int total)
    {
        if (!_processing) return;
        _translated = frame;
        if (!_comparing) _image.Source = frame;
        // Keep the picture readable: no dimming, just a small status pill at the bottom.
        _progress.Background = Brushes.Transparent;
        _progressContents.VerticalAlignment = VerticalAlignment.Bottom;
        _status.Text = $"翻译中 {completed}/{total}";
    }
    public void ShowResult(SKBitmap result)
    {
        var owned = result.Copy();
        _output?.Dispose(); _output = owned;
        _translated = BitmapInterop.ToBitmapSource(result);
        _processing = false;
        _image.Source = _comparing ? _original : _translated;
        _progress.Visibility = Visibility.Collapsed;
        _toolbar.IsOpen = true;
    }
    private void Compare(bool original)
    {
        _comparing = original;
        _image.Source = original ? _original : _translated ?? _original;
    }
    private async Task CopyAsync()
    {
        if (_output is null) return;
        try { await _clipboard.SetImageAsync(_output); }
        catch { MessageBox.Show(this, "剪贴板被其他程序占用，请稍后重试。", "屏译", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async Task SaveAsync()
    {
        if (_output is null) return;
        string folder = Environment.ExpandEnvironmentVariables(_settings?.ImageTranslate.SaveFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapTrans"));
        var dialog = new SaveFileDialog { Title = "保存译图", Filter = "PNG 图像|*.png", FileName = $"SnapTrans_{DateTime.Now:yyyyMMdd_HHmmss}.png", InitialDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AddExtension = true, DefaultExt = ".png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var encoded = _output.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(dialog.FileName, encoded.ToArray());
        }
        catch { MessageBox.Show(this, "保存失败，请检查目录是否可写。", "屏译", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
