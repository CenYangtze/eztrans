using System.Text;

namespace SnapTrans.Core.Llm;

/// <summary>Hides reasoning even when a tag is split across any number of chunks.</summary>
public sealed class ThinkFilter
{
    private readonly StringBuilder pending = new();
    private bool thinking;
    public string Push(string chunk)
    {
        pending.Append(chunk);
        var output = new StringBuilder();
        while (pending.Length > 0)
        {
            var tag = thinking ? "</think>" : "<think>";
            var value = pending.ToString();
            var index = value.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                if (!thinking) output.Append(value.AsSpan(0, index));
                pending.Remove(0, index + tag.Length);
                thinking = !thinking;
                continue;
            }
            var keep = 0;
            for (var n = 1; n < tag.Length && n <= value.Length; n++)
                if (value.EndsWith(tag[..n], StringComparison.OrdinalIgnoreCase)) keep = n;
            if (!thinking) output.Append(value.AsSpan(0, value.Length - keep));
            pending.Remove(0, value.Length - keep);
            break;
        }
        return output.ToString();
    }
    public string Finish()
    {
        var result = thinking ? "" : pending.ToString();
        pending.Clear();
        return result;
    }
}
