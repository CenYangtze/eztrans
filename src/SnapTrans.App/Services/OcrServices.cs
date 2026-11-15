using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;

namespace SnapTrans.App.Services;
public sealed class OcrServices(AppSettings settings) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (HttpClient Http, OcrModelManager Manager)> _managers = [];
    public OcrModelManager GetModelManager()
    {
        lock (_gate)
        {
            string root = string.IsNullOrWhiteSpace(settings.Ocr.ModelFolder) ? AppPaths.ModelFolder : Environment.ExpandEnvironmentVariables(settings.Ocr.ModelFolder);
            root = Path.GetFullPath(root);
            string key = root + "\0" + settings.Network.Proxy + "\0" + settings.Network.CustomProxyUrl;
            if (_managers.TryGetValue(key, out var entry)) return entry.Manager;
            var http = HttpTransport.Create(settings.Network);
            http.Timeout = TimeSpan.FromMinutes(20);
            var manager = new OcrModelManager(http, root);
            _managers.Add(key, (http, manager));
            return manager;
        }
    }
    public void Dispose() { lock (_gate) { foreach (var entry in _managers.Values) entry.Http.Dispose(); _managers.Clear(); } }
}
