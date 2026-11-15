using System.Runtime.InteropServices;
using SkiaSharp;
using SnapTrans.Core.Abstractions;
using SnapTrans.Platform.Windows.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SnapTrans.Platform.Windows;

/// <summary>One-frame, in-memory Windows Graphics Capture fallback for a black GDI monitor.</summary>
public sealed class WindowsGraphicsCapture
{
    public bool IsSupported => GraphicsCaptureSession.IsSupported();

    public Task<SKBitmap> CaptureMonitorAsync(MonitorInfo monitor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ct.ThrowIfCancellationRequested();
        // Device creation and GPU-to-CPU transfer must not occupy the WPF dispatcher.
        return Task.Run(() => CaptureCoreAsync(monitor, ct), ct);
    }

    private async Task<SKBitmap> CaptureCoreAsync(MonitorInfo monitor, CancellationToken ct)
    {
        if (!IsSupported) throw new NotSupportedException("当前 Windows 环境不支持 Graphics Capture 截图。");
        if (monitor.Bounds.IsEmpty) throw new ArgumentException("显示器尺寸无效。", nameof(monitor));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        using var device = GraphicsCaptureInterop.CreateDevice();
        var item = GraphicsCaptureInterop.CreateMonitorItem(monitor);
        var size = item.Size;
        if (size.Width != monitor.Bounds.Width || size.Height != monitor.Bounds.Height)
            throw new InvalidOperationException("显示器布局已改变，请重新截图。");
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var completion = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameGate = new object();
        Direct3D11CaptureFrame? retainedFrame = null;
        bool closing = false;
        void FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            Direct3D11CaptureFrame? frame = null;
            try
            {
                frame = sender.TryGetNextFrame();
                if (frame is not null)
                    lock (frameGate)
                        if (!closing && completion.TrySetResult(frame)) { retainedFrame = frame; frame = null; }
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { frame?.Dispose(); }
        }
        void ItemClosed(GraphicsCaptureItem sender, object args) => completion.TrySetException(new InvalidOperationException("显示器已断开或停止共享。"));
        pool.FrameArrived += FrameArrived;
        item.Closed += ItemClosed;
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        try
        {
            session.StartCapture();
            var frame = await completion.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                throw new InvalidOperationException("显示器分辨率已改变，请重新截图。");
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore).AsTask(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return CopyOpaque(bitmap, size.Width, size.Height);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Graphics Capture 截图超时；受保护的内容可能无法捕获。"); }
        finally
        {
            item.Closed -= ItemClosed;
            pool.FrameArrived -= FrameArrived;
            // Also covers a frame racing cancellation or an exception from StartCapture.
            lock (frameGate)
            {
                closing = true;
                retainedFrame?.Dispose();
                retainedFrame = null;
            }
        }
    }

    private static SKBitmap CopyOpaque(SoftwareBitmap source, int width, int height)
    {
        if (source.PixelWidth != width || source.PixelHeight != height)
            throw new InvalidOperationException("截取帧的像素尺寸与显示器不一致，请重新截图。");
        using var converted = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && source.BitmapAlphaMode == BitmapAlphaMode.Ignore
            ? null : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var bitmap = converted ?? source;
        uint count = checked((uint)(width * height * 4));
        var buffer = new global::Windows.Storage.Streams.Buffer(count) { Length = count };
        bitmap.CopyToBuffer(buffer);
        var bytes = new byte[count];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try
        {
            for (int index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
            Marshal.Copy(bytes, 0, output.GetPixels(), bytes.Length);
            return output;
        }
        catch { output.Dispose(); throw; }
        finally { Array.Clear(bytes); }
    }
}
