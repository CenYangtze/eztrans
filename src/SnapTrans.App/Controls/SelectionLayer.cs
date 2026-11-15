using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SnapTrans.Core.Geometry;

namespace SnapTrans.App.Controls;

/// <summary>All selection state is kept in physical pixels; drawing is the only DIP boundary.</summary>
public sealed class SelectionLayer : FrameworkElement
{
    private readonly double _scale;
    private readonly uint _dpi;
    private ImageSource? _image;
    private PixelPoint _anchor;
    private PixelRect _initial;
    private PixelRect _hover;
    private PixelPoint _pointer;
    private bool _pointerInside;
    private int _handle = -1;
    private bool _pressed, _dragging, _adjusting, _moving;
    public PixelRect Selection { get; private set; }
    public bool ConfirmMode { get; set; }
    public bool ShowMagnifier { get; set; }
    public PixelPoint CoordinateOrigin { get; set; }
    public Func<PixelPoint, PixelRect?>? WindowAtLocalPoint { get; set; }
    public bool IsAdjusting => _adjusting;
    public event EventHandler? Confirmed;
    public event EventHandler? Cancelled;
    public event EventHandler? SelectionChanged;
    public event EventHandler? FrameRendered;

    public SelectionLayer(uint dpi)
    {
        _dpi = dpi;
        _scale = dpi / 96d;
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        MouseEnter += (_, e) => { _pointer = Pixel(e); _pointerInside = true; InvalidateVisual(); };
        MouseLeave += (_, _) => { _pointerInside = false; if (!_pressed) _hover = default; InvalidateVisual(); };
    }
    public void Prepare(ImageSource image)
    {
        _image = image;
        Reset();
    }
    public void ClearImage() { _image = null; WindowAtLocalPoint = null; _pointerInside = false; Reset(); }
    public void Reset()
    {
        _pressed = _dragging = _adjusting = _moving = false;
        _handle = -1;
        Selection = default;
        _hover = default;
        ReleaseMouseCapture();
        Changed();
    }
    private PixelPoint Pixel(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return new(Math.Clamp(DpiMath.DipToPixel(p.X, _dpi), 0, PixelWidth), Math.Clamp(DpiMath.DipToPixel(p.Y, _dpi), 0, PixelHeight));
    }
    private int PixelWidth => Math.Max(1, DpiMath.DipToPixel(ActualWidth, _dpi));
    private int PixelHeight => Math.Max(1, DpiMath.DipToPixel(ActualHeight, _dpi));
    private Rect DipRect(PixelRect r) => new(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);
    private void Changed() { InvalidateVisual(); SelectionChanged?.Invoke(this, EventArgs.Empty); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _anchor = Pixel(e);
        _pointer = _anchor;
        if (_adjusting && e.ClickCount == 2 && Selection.Contains(_anchor)) { Confirmed?.Invoke(this, EventArgs.Empty); return; }
        _initial = Selection;
        _handle = _adjusting ? HitHandle(_anchor) : -1;
        _moving = _adjusting && _handle < 0 && Selection.Contains(_anchor);
        if (!_moving && _handle < 0) { _adjusting = false; Selection = default; }
        _pressed = true;
        _dragging = _moving || _handle >= 0;
        CaptureMouse();
        Changed();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = Pixel(e);
        _pointer = p;
        if (!_pressed)
        {
            int handle = _adjusting ? HitHandle(p) : -1;
            Cursor = handle switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE, _ => _adjusting && Selection.Contains(p) ? Cursors.SizeAll : Cursors.Cross };
            _hover = !_adjusting ? FindWindow(p) : default;
            InvalidateVisual();
            return;
        }
        if (!_dragging && (Math.Abs(p.X - _anchor.X) >= 3 * _scale || Math.Abs(p.Y - _anchor.Y) >= 3 * _scale)) _dragging = true;
        if (!_dragging) return;
        if (_moving)
            Selection = _initial with { X = Math.Clamp(_initial.X + p.X - _anchor.X, 0, PixelWidth - _initial.Width), Y = Math.Clamp(_initial.Y + p.Y - _anchor.Y, 0, PixelHeight - _initial.Height) };
        else if (_handle >= 0)
        {
            int left = _initial.Left, top = _initial.Top, right = _initial.Right, bottom = _initial.Bottom;
            if (_handle is 0 or 6 or 7) left = Math.Min(p.X, right - 8);
            if (_handle is 0 or 1 or 2) top = Math.Min(p.Y, bottom - 8);
            if (_handle is 2 or 3 or 4) right = Math.Max(p.X, left + 8);
            if (_handle is 4 or 5 or 6) bottom = Math.Max(p.Y, top + 8);
            Selection = new(left, top, right - left, bottom - top);
        }
        else Selection = PixelRect.FromPoints(_anchor, p);
        Changed();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();
        if (!_dragging)
        {
            var window = FindWindow(Pixel(e));
            if (window.Width >= 8 && window.Height >= 8) Selection = window;
            else { Reset(); return; }
        }
        if (Selection.Width < 8 || Selection.Height < 8) { Reset(); return; }
        _dragging = false;
        _moving = false;
        _handle = -1;
        if (ConfirmMode) { _adjusting = true; Changed(); }
        else Confirmed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_adjusting) Reset(); else Cancelled?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape) { Cancelled?.Invoke(this, EventArgs.Empty); return true; }
        if (!_adjusting) return false;
        if (key == Key.Enter) { Confirmed?.Invoke(this, EventArgs.Empty); return true; }
        int step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
        if (dx == 0 && dy == 0) return false;
        Selection = modifiers.HasFlag(ModifierKeys.Control)
            ? Selection with { Width = Math.Clamp(Selection.Width + dx, 8, PixelWidth - Selection.X), Height = Math.Clamp(Selection.Height + dy, 8, PixelHeight - Selection.Y) }
            : Selection with { X = Math.Clamp(Selection.X + dx, 0, PixelWidth - Selection.Width), Y = Math.Clamp(Selection.Y + dy, 0, PixelHeight - Selection.Height) };
        Changed();
        return true;
    }
    private PixelPoint[] Handles() => [new(Selection.Left, Selection.Top), new(Selection.Left + Selection.Width / 2, Selection.Top), new(Selection.Right, Selection.Top), new(Selection.Right, Selection.Top + Selection.Height / 2), new(Selection.Right, Selection.Bottom), new(Selection.Left + Selection.Width / 2, Selection.Bottom), new(Selection.Left, Selection.Bottom), new(Selection.Left, Selection.Top + Selection.Height / 2)];
    private int HitHandle(PixelPoint p)
    {
        var points = Handles();
        for (int i = 0; i < points.Length; i++) if (Math.Abs(points[i].X - p.X) <= 6 * _scale && Math.Abs(points[i].Y - p.Y) <= 6 * _scale) return i;
        return -1;
    }
    private PixelRect FindWindow(PixelPoint point)
        => (WindowAtLocalPoint?.Invoke(point) ?? default).Intersect(new PixelRect(0, 0, PixelWidth, PixelHeight));
    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawImage(_image, full);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, full);
        PixelRect highlighted = Selection.IsEmpty ? _hover : Selection;
        if (!highlighted.IsEmpty) DrawSelection(dc, full, highlighted);
        if (ShowMagnifier && _pointerInside && _image is not null) DrawMagnifier(dc);
        FrameRendered?.Invoke(this, EventArgs.Empty);
    }
    private void DrawSelection(DrawingContext dc, Rect full, PixelRect highlighted)
    {
        var rect = DipRect(highlighted);
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawImage(_image, full);
        dc.Pop();
        var accent = new SolidColorBrush(Color.FromRgb(72, 151, 255));
        dc.DrawRectangle(null, new Pen(accent, 1 / _scale), rect);
        if (_adjusting)
            foreach (var point in Handles()) dc.DrawRectangle(Brushes.White, new Pen(accent, 1 / _scale), new Rect(point.X / _scale - 3, point.Y / _scale - 3, 6, 6));
        var text = new FormattedText($"{highlighted.Width} × {highlighted.Height} px{(Selection.IsEmpty ? " · 单击选中窗口" : "")}", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, _scale);
        double y = rect.Bottom + 8 + text.Height < ActualHeight ? rect.Bottom + 8 : Math.Max(4, rect.Top - text.Height - 8);
        var label = new Rect(Math.Clamp(rect.Left, 0, Math.Max(0, ActualWidth - text.Width - 16)), y, text.Width + 16, text.Height + 8);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 25, 35)), null, label, 4, 4);
        dc.DrawText(text, new Point(label.Left + 8, label.Top + 4));
    }
    private void DrawMagnifier(DrawingContext dc)
    {
        const int samples = 15;
        double size = 120 / _scale;
        double left = _pointer.X / _scale + 22;
        double top = _pointer.Y / _scale + 22;
        if (left + size + 8 > ActualWidth) left = _pointer.X / _scale - size - 22;
        if (top + size + 38 > ActualHeight) top = _pointer.Y / _scale - size - 46;
        left = Math.Clamp(left, 0, Math.Max(0, ActualWidth - size));
        top = Math.Clamp(top, 0, Math.Max(0, ActualHeight - size - 28));
        double sourceX = Math.Clamp(_pointer.X - samples / 2, 0, Math.Max(0, PixelWidth - samples));
        double sourceY = Math.Clamp(_pointer.Y - samples / 2, 0, Math.Max(0, PixelHeight - samples));
        var brush = new ImageBrush(_image) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(sourceX, sourceY, samples, samples), Stretch = Stretch.Fill };
        var target = new Rect(left, top, size, size);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 25, 35)), null, new Rect(left - 2, top - 2, size + 4, size + 30), 3, 3);
        dc.DrawRectangle(brush, new Pen(Brushes.White, 1 / _scale), target);
        double centerX = left + (_pointer.X - sourceX + .5) * size / samples;
        double centerY = top + (_pointer.Y - sourceY + .5) * size / samples;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 76, 76)), 1 / _scale);
        dc.DrawLine(pen, new Point(centerX, top), new Point(centerX, top + size));
        dc.DrawLine(pen, new Point(left, centerY), new Point(left + size, centerY));
        var coordinate = new FormattedText($"{_pointer.X + CoordinateOrigin.X}, {_pointer.Y + CoordinateOrigin.Y}", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, _scale);
        dc.DrawText(coordinate, new Point(left + 5, top + size + 5));
    }
}
