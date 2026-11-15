using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SkiaSharp;
using SnapTrans.App.Controls;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Windows;

public sealed record SelectionResult(int MonitorIndex, PixelRect RegionLocal, RequestedAction Action);
public sealed record SelectionAssistOptions(bool Magnifier = false, Func<PixelPoint, PixelRect?>? WindowAtPoint = null);

public sealed class OverlayWindow : Window
{
    private readonly SelectionLayer _selection;
    private readonly StackPanel _toolbar;
    private readonly Border _toolbarSurface;
    private RequestedAction _action;
    private bool _finishing;
    private bool _firstFramePending;
    public MonitorInfo Monitor { get; }
    public event EventHandler<SelectionResult?>? SelectionCompleted;
    public event EventHandler? FirstFrameRendered;

    public OverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        Title = "屏译 · 框选屏幕";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Width = monitor.Bounds.Width / monitor.Scale;
        Height = monitor.Bounds.Height / monitor.Scale;
        var root = new Grid();
        _selection = new SelectionLayer(monitor.Dpi);
        root.Children.Add(_selection);
        _toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        _toolbarSurface = new Border
        {
            Child = _toolbar, CornerRadius = new CornerRadius(10), Padding = new Thickness(5),
            Background = new SolidColorBrush(Color.FromArgb(242, 31, 35, 43)), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 32), Visibility = Visibility.Collapsed
        };
        AddAction("翻译", "Enter", RequestedAction.Floating);
        AddAction("译图", "Ctrl+Enter", RequestedAction.Image);
        AddAction("复制文字", "Ctrl+C", RequestedAction.CopyText);
        AddAction("查词", "Ctrl+D", RequestedAction.Dictionary);
        var cancel = Tool("取消", "Esc");
        cancel.Click += (_, _) => Finish(null);
        _toolbar.Children.Add(cancel);
        root.Children.Add(_toolbarSurface);
        Content = root;
        _selection.Confirmed += (_, _) => Complete(_action);
        _selection.Cancelled += (_, _) => Finish(null);
        _selection.SelectionChanged += (_, _) => _toolbarSurface.Visibility = _selection.IsAdjusting ? Visibility.Visible : Visibility.Collapsed;
        _selection.FrameRendered += (_, _) =>
        {
            if (!_firstFramePending || !IsVisible) return;
            _firstFramePending = false;
            FirstFrameRendered?.Invoke(this, EventArgs.Empty);
        };
        SourceInitialized += (_, _) =>
        {
            WindowPlacement.SetBounds(this, monitor.Bounds);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (_selection.IsAdjusting && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Enter or Key.C or Key.D)
            {
                Complete(e.Key switch { Key.C => RequestedAction.CopyText, Key.D => RequestedAction.Dictionary, _ => RequestedAction.Image });
                e.Handled = true;
            }
            else e.Handled = _selection.HandleKey(e.Key, Keyboard.Modifiers);
        };
    }
    private void AddAction(string label, string shortcut, RequestedAction action)
    {
        var button = Tool(label, shortcut);
        button.Click += (_, _) => Complete(action);
        _toolbar.Children.Add(button);
    }
    private static Button Tool(string label, string shortcut)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = shortcut, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, MinHeight = 34, Margin = new Thickness(1, 0, 1, 0), Padding = new Thickness(14, 0, 14, 0) };
        button.SetResourceReference(StyleProperty, "Button.Overlay");
        return button;
    }
    public void Prepare(SKBitmap bitmap, bool confirmMode, RequestedAction action, SelectionAssistOptions? assist = null)
    {
        _finishing = false;
        _action = action;
        _selection.ConfirmMode = confirmMode;
        _selection.ShowMagnifier = assist?.Magnifier ?? false;
        _selection.CoordinateOrigin = new(Monitor.Bounds.X, Monitor.Bounds.Y);
        _selection.WindowAtLocalPoint = assist?.WindowAtPoint is { } find ? local =>
        {
            var bounds = find(new PixelPoint(local.X + Monitor.Bounds.X, local.Y + Monitor.Bounds.Y));
            return bounds is { } rect ? rect with { X = rect.X - Monitor.Bounds.X, Y = rect.Y - Monitor.Bounds.Y } : null;
        } : null;
        _selection.Prepare(BitmapInterop.ToBitmapSource(bitmap));
    }
    public void ShowCapture()
    {
        _finishing = false;
        _firstFramePending = true;
        Show();
        WindowPlacement.SetBounds(this, Monitor.Bounds);
        Activate();
        _selection.Focus();
        _selection.InvalidateVisual();
    }
    public void ShowCapture(SKBitmap bitmap, bool confirmMode, RequestedAction action) { Prepare(bitmap, confirmMode, action); ShowCapture(); }
    public void HideCapture() { _finishing = true; _firstFramePending = false; _selection.ClearImage(); Hide(); }
    private void Complete(RequestedAction action)
    {
        if (_selection.Selection.Width >= 8 && _selection.Selection.Height >= 8)
            Finish(new SelectionResult(Monitor.Index, _selection.Selection, action));
    }
    private void Finish(SelectionResult? result)
    {
        if (_finishing) return;
        _finishing = true;
        Hide();
        SelectionCompleted?.Invoke(this, result);
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x02E0)
        {
            handled = true;
            Dispatcher.BeginInvoke(() => WindowPlacement.SetBounds(this, Monitor.Bounds));
        }
        return 0;
    }
}
