using System.Text;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Ocr;

namespace SnapTrans.Core.Dictionaries;

/// <summary>Turns OCR output into a dictionary query: the word under the pointer, or the text of a drawn selection.</summary>
public static class WordPicker
{
    public const int MaxQueryLength = 80;

    /// <summary>A word found on the page; <see cref="Bounds"/> is in page pixels.</summary>
    public sealed record Hit(string Word, PixelRectF Bounds);

    /// <summary>
    /// The Latin-script word closest to the point. OCR engines may return a whole line or a word with
    /// punctuation attached as one item, so each item is split into words whose boxes follow character position.
    /// </summary>
    public static Hit? Nearest(OcrPage page, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(page);
        Hit? best = null;
        double bestDistance = double.MaxValue, bestCenter = double.MaxValue;
        foreach (var line in page.Lines)
            foreach (var item in line.Words.Count > 0 ? line.Words : [new OcrWord(line.Text, line.Bounds)])
                foreach (var hit in Split(item))
                {
                    double dx = Math.Max(Math.Max(hit.Bounds.Left - x, x - hit.Bounds.Right), 0);
                    double dy = Math.Max(Math.Max(hit.Bounds.Top - y, y - hit.Bounds.Bottom), 0);
                    double distance = dx * dx + dy * dy;
                    double cx = hit.Bounds.Left + hit.Bounds.Width / 2 - x, cy = hit.Bounds.Top + hit.Bounds.Height / 2 - y;
                    double center = cx * cx + cy * cy;
                    if (distance < bestDistance || (distance == bestDistance && center < bestCenter))
                    { best = hit; bestDistance = distance; bestCenter = center; }
                }
        return best;
    }

    /// <summary>All recognized text as one query, for a region the user drew around a word or short phrase.</summary>
    public static string? Phrase(OcrPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Normalize(OcrText.JoinLines(page.Lines.Select(line => line.Text)));
    }

    /// <summary>
    /// Collapses whitespace, drops punctuation around the text and limits the length at a word boundary.
    /// Returns null when nothing with a letter remains.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' '); }
            else builder.Append(c is '’' or '‘' ? '\'' : c);
        }
        int start = 0, end = builder.Length;
        while (start < end && !char.IsLetterOrDigit(builder[start])) start++;
        while (end > start && !char.IsLetterOrDigit(builder[end - 1])) end--;
        string value = builder.ToString(start, end - start);
        if (value.Length > MaxQueryLength)
        {
            int cut = value.LastIndexOf(' ', MaxQueryLength);
            value = value[..(cut > 0 ? cut : MaxQueryLength)].TrimEnd();
        }
        return value.Any(char.IsLetter) ? value : null;
    }

    private static IEnumerable<Hit> Split(OcrWord item)
    {
        string text = item.Text;
        if (string.IsNullOrEmpty(text)) yield break;
        int index = 0;
        while (index < text.Length)
        {
            if (!IsWordLetter(text[index])) { index++; continue; }
            int start = index;
            while (index < text.Length && (IsWordLetter(text[index]) || (IsJoiner(text[index]) && index + 1 < text.Length && IsWordLetter(text[index + 1])))) index++;
            float left = item.Bounds.Left + item.Bounds.Width * start / text.Length;
            float width = item.Bounds.Width * (index - start) / text.Length;
            yield return new Hit(text[start..index].Replace('’', '\''), new PixelRectF(left, item.Bounds.Top, width, item.Bounds.Height));
        }
    }

    // English → Chinese lookups only: CJK characters and digits never start or continue a word.
    private static bool IsWordLetter(char c) => char.IsLetter(c) && !OcrText.IsCjk(c);
    private static bool IsJoiner(char c) => c is '-' or '\'' or '’';
}
