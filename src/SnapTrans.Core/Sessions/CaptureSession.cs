using SkiaSharp;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using SnapTrans.Core.Ocr;

namespace SnapTrans.Core.Sessions;

/// <summary>
/// Result windows retain ownership; active operations retain only resource leases.
/// The last owner cancels work, while the last lease releases the bitmap.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private readonly Dictionary<(string, OcrRequest), Task<OcrPage>> _ocr = [];
    private readonly object _gate = new();
    private int _references = 1;
    private int _owners = 1;
    private bool _initialOwnerReleased;
    private bool _closed;
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public required MonitorInfo Monitor { get; init; }
    public required PixelRect RegionVirtual { get; init; }
    public required SKBitmap Image { get; init; }
    public required RequestedAction Action { get; init; }
    public CancellationTokenSource Cts { get; } = new();

    public Task<OcrPage> GetOcrAsync(IOcrEngine engine, OcrRequest request)
    {
        TaskCompletionSource<OcrPage> completion;
        IDisposable lease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_ocr.TryGetValue((engine.Id, request), out var cached) && !cached.IsCanceled && !cached.IsFaulted)
                return cached;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ocr[(engine.Id, request)] = completion.Task;
            _references++;
            lease = new Lease(this, false);
        }
        // Do not use the session token to schedule this task: cancellation before startup
        // must still enter the worker's finally block and release its resource lease.
        // OCR engines may synchronously resize/convert pixels before their first await.
        try { _ = Task.Run(() => RecognizeAsync(engine, request, lease, completion)); }
        catch (Exception error) { completion.TrySetException(error); lease.Dispose(); }
        return completion.Task;
    }

    private async Task RecognizeAsync(IOcrEngine engine, OcrRequest request, IDisposable lease, TaskCompletionSource<OcrPage> completion)
    {
        OcrPage? result = null;
        Exception? failure = null;
        try
        {
            Cts.Token.ThrowIfCancellationRequested();
            result = await engine.RecognizeAsync(Image, request, Cts.Token).ConfigureAwait(false);
            Cts.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { failure = error; }
        try { lease.Dispose(); }
        catch (Exception error) { failure ??= error; }
        if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
        else if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(result!);
    }

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _references++;
            return new Lease(this, false);
        }
    }
    /// <summary>Keep the capture and cached OCR usable after the originating window closes.</summary>
    public IDisposable RetainOwner()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _references++; _owners++;
            return new Lease(this, true);
        }
    }
    private sealed class Lease(CaptureSession session, bool owner) : IDisposable
    {
        private CaptureSession? _session = session;
        public void Dispose()
        {
            var session = Interlocked.Exchange(ref _session, null);
            if (owner) session?.ReleaseOwner(); else session?.Release();
        }
    }
    private void Release()
    {
        bool dispose;
        lock (_gate) { dispose = --_references == 0; }
        if (dispose)
        {
            try { Image.Dispose(); }
            finally { Cts.Dispose(); }
        }
    }
    private void ReleaseOwner()
    {
        bool cancel;
        lock (_gate)
        {
            cancel = --_owners == 0;
            if (cancel) _closed = true;
        }
        // Cancellation invokes external callbacks synchronously. Never hold _gate
        // across it, and retain this owner's reference until callbacks have finished.
        try { if (cancel) Cts.Cancel(); }
        finally { Release(); }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_initialOwnerReleased) return;
            _initialOwnerReleased = true;
        }
        ReleaseOwner();
    }
}
