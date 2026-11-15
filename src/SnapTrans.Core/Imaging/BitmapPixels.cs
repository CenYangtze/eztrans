using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SnapTrans.Core.Imaging;

/// <summary>BGRA screenshots have the same little-endian storage as SKColor; avoid one native call per pixel.</summary>
internal readonly ref struct BitmapPixels
{
    private readonly SKBitmap _bitmap;
    private readonly Span<SKColor> _colors;
    private readonly int _stride;
    private readonly bool _direct;
    public BitmapPixels(SKBitmap bitmap)
    {
        _bitmap = bitmap;
        _direct = BitConverter.IsLittleEndian && bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Opaque;
        _colors = _direct ? MemoryMarshal.Cast<byte, SKColor>(bitmap.GetPixelSpan()) : [];
        _stride = bitmap.RowBytes / 4;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKColor Get(int x, int y) => _direct ? _colors[y * _stride + x] : _bitmap.GetPixel(x, y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int x, int y, SKColor color)
    {
        if (_direct) _colors[y * _stride + x] = color;
        else _bitmap.SetPixel(x, y, color);
    }
}
