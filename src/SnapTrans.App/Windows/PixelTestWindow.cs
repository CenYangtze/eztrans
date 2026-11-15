using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SnapTrans.Core.Abstractions;
using SnapTrans.Platform.Windows;

namespace SnapTrans.App.Windows;

/// <summary>One physical-pixel chessboard, intentionally independent of WPF's scaling.</summary>
public sealed class PixelTestWindow : Window
{
    public PixelTestWindow(MonitorInfo monitor)
    {
        Title = "屏译 · 像素测试图（Esc 关闭）";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = monitor.Bounds.Width / monitor.Scale;
        Height = monitor.Bounds.Height / monitor.Scale;
        using var bitmap = new SKBitmap(monitor.Bounds.Width, monitor.Bounds.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.White : SKColors.Black);
        using (var canvas = new SKCanvas(bitmap))
        using (var backdrop = new SKPaint { Color = new SKColor(20, 30, 45) })
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        using (var font = new SKFont(SKTypeface.Default, 13))
        {
            for (int y = 0; y < bitmap.Height; y += 100)
                for (int x = 0; x < bitmap.Width; x += 100)
                {
                    canvas.DrawRect(x, y, 97, 21, backdrop);
                    canvas.DrawText($"{x + monitor.Bounds.X},{y + monitor.Bounds.Y}", x + 3, y + 15, font, paint);
                }
        }
        var image = new System.Windows.Controls.Image { Source = BitmapInterop.ToBitmapSource(bitmap), Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Content = image;
        SourceInitialized += (_, _) => WindowPlacement.SetBounds(this, monitor.Bounds);
        Loaded += (_, _) => WindowPlacement.SetBounds(this, monitor.Bounds);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
