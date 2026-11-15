using SkiaSharp;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Hotkeys;

namespace SnapTrans.Core.Abstractions;

public sealed record MonitorInfo(int Index, string DeviceName, PixelRect Bounds, PixelRect WorkArea, uint Dpi, bool IsPrimary)
{
    public double Scale => Dpi / 96.0;
}
public interface IMonitorService { IReadOnlyList<MonitorInfo> GetMonitors(); }
public interface IScreenCapture
{
    IReadOnlyList<MonitorCapture> CaptureAll(IReadOnlyList<MonitorInfo> monitors);
    /// <summary>Captures one rectangle given in virtual-screen pixels; the caller owns the bitmap.</summary>
    SKBitmap CaptureRegion(PixelRect region);
}
public sealed record MonitorCapture(MonitorInfo Monitor, SKBitmap Bitmap);
public enum HotkeyId { TranslateFloating = 1, TranslateImage = 2, RepeatLastRegion = 3, Dictionary = 4 }
public enum RequestedAction { Floating, Image, CopyText, Dictionary }
public interface IHotkeyService
{
    event EventHandler<HotkeyId>? Pressed;
    bool TryRegister(HotkeyId id, HotkeyGesture gesture, out string? error);
    void Unregister(HotkeyId id);
    void UnregisterAll();
}
public interface IClipboardService { Task SetTextAsync(string text); Task SetImageAsync(SKBitmap image, CancellationToken ct = default); }
public interface ISecretProtector { string Protect(string plainText); string Unprotect(string protectedBase64); }
public interface INotifier { void Info(string message); void Error(string message, Exception? exception = null); }
