using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using SkiaSharp;
using SnapTrans.Core.Abstractions;

namespace SnapTrans.Platform.Windows;

public sealed class ClipboardService : IClipboardService
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    public Task SetTextAsync(string text) => OnDispatcher(async () =>
    {
        var data = new DataObject();
        data.SetText(text ?? "", TextDataFormat.UnicodeText);
        await SetWithRetryAsync(data);
    });

    public async Task SetImageAsync(SKBitmap image, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Clone before posting to the UI dispatcher: the caller may release its render result.
        using var copy = image.Copy();
        await OnDispatcher(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using (var encoded = copy.Encode(SKEncodedImageFormat.Png, 100))
            using (var png = new MemoryStream(encoded.ToArray(), writable: false))
            using (var dib = CreateDib(copy))
            {
                var data = new DataObject();
                data.SetData(DataFormats.Bitmap, BitmapInterop.ToBitmapSource(copy), autoConvert: true);
                data.SetData(DataFormats.Dib, dib, autoConvert: false);
                data.SetData("PNG", png, autoConvert: false);
                await SetWithRetryAsync(data, ct);
            }
        });
    }

    private Task OnDispatcher(Func<Task> action) => dispatcher.CheckAccess() ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();

    private static async Task SetWithRetryAsync(DataObject data, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch (COMException) when (attempt < 4) { await Task.Delay(50, ct); }
        }
    }

    private static MemoryStream CreateDib(SKBitmap image)
    {
        using var normalized = new SKBitmap(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(normalized)) canvas.DrawBitmap(image, 0, 0);
        var stride = checked(image.Width * 4);
        var pixelBytes = checked(stride * image.Height);
        var stream = new MemoryStream(checked(40 + pixelBytes));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(40u); writer.Write(image.Width); writer.Write(image.Height);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0u);
            writer.Write((uint)pixelBytes); writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0u);
            var row = new byte[stride];
            // CF_DIB with positive height is bottom-up; no BITMAPFILEHEADER is included.
            for (int y = image.Height - 1; y >= 0; y--)
            {
                Marshal.Copy(normalized.GetPixels() + y * normalized.RowBytes, row, 0, stride);
                for (int x = 3; x < row.Length; x += 4) row[x] = 255;
                writer.Write(row);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
