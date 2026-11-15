using System.Runtime.CompilerServices;
using System.Text;

namespace SnapTrans.Core.Llm;

public sealed record SseEvent(string? Event, string Data);

public static class SseReader
{
    // SSE is line-oriented, independent of HTTP packet/chunk boundaries. A multiline data
    // field is joined with LF as prescribed by the event-stream specification.
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default, TimeSpan? idleTimeout = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var data = new StringBuilder();
        string? eventName = null;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(idleTimeout ?? TimeSpan.FromSeconds(30));
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new LlmException("流式响应等待超时，请重试"); }
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventName, data.ToString().TrimEnd('\n'));
                    data.Clear();
                }
                eventName = null;
                if (line is null) yield break;
                continue;
            }
            line = line.TrimStart();
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            if (field == "event") eventName = value;
            if (field == "data")
            {
                data.Append(value).Append('\n');
                if (data.Length > 4 * 1024 * 1024)
                    throw new LlmException("服务返回的流式事件过大");
            }
        }
    }
}
