using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace SnapTrans.App.Services;

/// <summary>Per-user IPC; a second invocation can only ask the running instance to open settings, its tray menu or a word lookup.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    public bool IsFirst { get; }

    public SingleInstance()
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = "SnapTrans." + user.Replace('-', '_');
        _mutex = new Mutex(true, @"Local\" + _pipeName, out bool created);
        IsFirst = created;
    }

    public const string Settings = "settings", Menu = "menu", Lookup = "lookup";
    private const string LookupPrefix = Lookup + ":";
    private const int MaxWordLength = 48;

    /// <summary>The command for "--lookup [word]"; a word that is not plain letters falls back to the hotkey behaviour.</summary>
    public static string LookupCommand(string? word) => TryWord(word, out string clean) ? LookupPrefix + clean : Lookup;

    /// <summary>The word carried by a lookup command, or null for the bare command.</summary>
    public static string? LookupWord(string command) => command.StartsWith(LookupPrefix, StringComparison.Ordinal) ? command[LookupPrefix.Length..] : null;

    // Only ASCII letters with single spaces, hyphens or apostrophes travel through the pipe.
    private static bool TryWord(string? word, out string clean)
    {
        clean = (word ?? "").Trim();
        return clean.Length is > 0 and <= MaxWordLength && char.IsAsciiLetter(clean[0]) && clean.All(c => char.IsAsciiLetter(c) || c is ' ' or '-' or '\'');
    }

    public async Task NotifyAsync(string command = Settings)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }

    public async Task ListenAsync(Action<string> onCommand)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                // One bounded message; never evaluate commands or paths received through IPC.
                byte[] buffer = new byte[96];
                using var messageTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                messageTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                int count = 0;
                while (count < buffer.Length)
                {
                    int read = await server.ReadAsync(buffer.AsMemory(count), messageTimeout.Token);
                    if (read == 0) break;
                    count += read;
                    if (Array.IndexOf(buffer, (byte)'\n', 0, count) >= 0) break;
                }
                // Only the fixed words, or a lookup of one validated plain word, are accepted; anything else is ignored.
                string command = Encoding.UTF8.GetString(buffer, 0, count).Trim();
                if (command is Settings or Menu or Lookup || (LookupWord(command) is { } word && TryWord(word, out _))) onCommand(command);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _stop.Dispose();
    }
}
