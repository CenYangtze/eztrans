using SkiaSharp;

namespace SnapTrans.Core.Imaging;

/// <summary>Owns typefaces and resolves fallback fonts once for every Unicode scalar.</summary>
public sealed class FontResolver : IDisposable
{
    private readonly SKTypeface _primary;
    private readonly Dictionary<int, SKTypeface?> _fallbacks = [];
    private readonly Dictionary<string, IReadOnlyList<(string Text, SKTypeface Typeface)>> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<(SKTypeface Face, float Size, float Scale), SKFont> _fonts = [];
    private readonly Dictionary<(string Text, float Size, float Scale), float> _measurements = [];
    private readonly Dictionary<string, (float Ascent, float Descent)> _ratios = new(StringComparer.Ordinal);
    public FontResolver(string family, bool bold = false)
    {
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        _primary = SKFontManager.Default.MatchFamily(family, style) ?? SKFontManager.Default.MatchFamily("Segoe UI", style) ??
            SKFontManager.Default.MatchFamily("DejaVu Sans", style) ?? SKTypeface.Default;
    }

    public IReadOnlyList<(string Text, SKTypeface Typeface)> Runs(string text)
    {
        if (_runs.TryGetValue(text, out var cached)) return cached;
        var result = new List<(string Text, SKTypeface Typeface)>();
        using var probe = new SKFont(_primary, 12);
        foreach (var rune in text.EnumerateRunes())
        {
            string part = rune.ToString();
            SKTypeface face = _primary;
            if (!probe.ContainsGlyphs(part))
            {
                if (!_fallbacks.TryGetValue(rune.Value, out var fallback))
                    _fallbacks[rune.Value] = fallback = SKFontManager.Default.MatchCharacter(rune.Value);
                face = fallback ?? _primary;
            }
            if (result.Count > 0 && ReferenceEquals(result[^1].Typeface, face))
                result[^1] = (result[^1].Text + part, face);
            else result.Add((part, face));
        }
        _runs[text] = result;
        return result;
    }

    public float Measure(string text, float fontSize, float scaleX = 1)
    {
        if (_measurements.TryGetValue((text, fontSize, scaleX), out float cached)) return cached;
        float width = 0;
        foreach (var run in Runs(text))
        {
            if (!_fonts.TryGetValue((run.Typeface, fontSize, scaleX), out var font))
                _fonts[(run.Typeface, fontSize, scaleX)] = font = CreateFont(run.Typeface, fontSize, scaleX);
            width += font.MeasureText(run.Text);
        }
        _measurements[(text, fontSize, scaleX)] = width;
        return width;
    }

    public (float Ascent, float Descent) InkRatios(string language)
    {
        if (_ratios.TryGetValue(language, out var cached)) return cached;
        string reference = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || language is "ja" or "ko" ? "国永字" : "Hgy";
        float top = 0, bottom = 0;
        foreach (var run in Runs(reference))
        {
            using var font = CreateFont(run.Typeface, 100, 1);
            font.MeasureText(run.Text, out var bounds);
            top = Math.Min(top, bounds.Top);
            bottom = Math.Max(bottom, bounds.Bottom);
        }
        if (bottom - top < 1)
        {
            using var font = CreateFont(_primary, 100, 1);
            return _ratios[language] = (Math.Abs(font.Metrics.Ascent) / 100, Math.Max(0, font.Metrics.Descent) / 100);
        }
        return _ratios[language] = (-top / 100, bottom / 100);
    }

    internal static SKFont CreateFont(SKTypeface face, float size, float scaleX) => new(face, size)
    {
        ScaleX = scaleX, Edging = SKFontEdging.Antialias, Subpixel = true
    };

    public void Dispose()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        foreach (var font in _fallbacks.Values.Where(f => f is not null).Distinct()) font!.Dispose();
        if (!ReferenceEquals(_primary, SKTypeface.Default)) _primary.Dispose();
    }
}
