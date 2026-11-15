using System.IO;
using System.Reflection;
using SkiaSharp;
using SnapTrans.Core.Ocr;

namespace SnapTrans.Platform.Windows;

/// <summary>Resolve settings at request time; optional ONNX/native dependencies never enter the base app.</summary>
public sealed class OcrEngineRegistry : IOcrEngine, IDisposable
{
    private readonly WindowsOcrEngine windows;
    private readonly Func<string> engineId;
    private readonly Func<OcrModelManager> models;
    private readonly Func<string> presetId;
    private readonly string pluginFolder;
    private readonly Dictionary<string, IOcrEngine> instances = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private IOcrEnginePlugin? plugin;
    private OcrPluginLoader? loader;
    private bool disposed;
    public OcrEngineRegistry(WindowsOcrEngine windows, Func<string> engineId, Func<OcrModelManager> models, Func<string> presetId, string? pluginFolder = null)
    {
        this.windows = windows; this.engineId = engineId; this.models = models; this.presetId = presetId;
        this.pluginFolder = Path.GetFullPath(pluginFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapTrans", "plugins", "paddle"));
    }
    // Include model identity in the session OCR cache key when a user changes the preset.
    public string Id => engineId() == "paddle" ? "paddle:" + presetId() + ":" + models().RootFolder : windows.Id;
    public IReadOnlyList<string> InstalledLanguages
    {
        get
        {
            try
            {
                if (engineId() != "paddle") return windows.InstalledLanguages;
                var preset = OcrModelCatalog.Get(presetId());
                using var inspector = new OcrPluginLoader(pluginFolder);
                return inspector.ProbeAvailability().IsAvailable && models().HasAllFiles(preset.Id) ? preset.Languages : [];
            }
            catch (Exception exception) when (IsPluginError(exception)) { return []; }
        }
    }
    public OcrAvailability CheckAvailability(string? languageTag)
    {
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (engineId() != "paddle") return windows.CheckAvailability(languageTag);
            var preset = OcrModelCatalog.Get(presetId());
            using var inspector = new OcrPluginLoader(pluginFolder);
            var probe = inspector.ProbeAvailability();
            if (!probe.IsAvailable) return new(false, probe.Message, InstallInstructions());
            if (!preset.SupportsLanguage(languageTag)) return new(false, "所选 PaddleOCR 模型不支持此语言，请更换配套模型或使用 Windows OCR。");
            return models().HasAllFiles(preset.Id) ? new(true) : new(false, "OCR 模型尚未下载完整。", "请在设置 → 文字识别中下载模型。");
        }
        catch (Exception exception) when (IsPluginError(exception))
        { return new(false, "PaddleOCR 可选组件尚未安装完整或无法加载。", InstallInstructions()); }
    }
    public Task<OcrPage> RecognizeAsync(SKBitmap image, OcrRequest request, CancellationToken ct) => Active().RecognizeAsync(image, request, ct);
    /// <summary>Freeze the engine/model identity for one request before dispatching background work.</summary>
    public IOcrEngine Snapshot() => Active();
    public IOcrEngine GetCurrentEngine() => Snapshot();
    public string GetPluginStatus()
    {
        lock (sync)
        {
            using var inspector = new OcrPluginLoader(pluginFolder);
            var probe = inspector.ProbeAvailability();
            return probe.Message + "\n组件目录：" + pluginFolder + (plugin is null ? "\n状态检查不会加载组件；可以直接导入升级包。" : "\n组件已被本次进程加载。更新前请切换 Windows OCR 并重启程序，再安装组件包。");
        }
    }
    public bool IsPluginAvailable
    {
        get { lock (sync) { using var inspector = new OcrPluginLoader(pluginFolder); return !disposed && inspector.ProbeAvailability().IsAvailable; } }
    }
    private string InstallInstructions() => "请从官方发布页获取匹配架构的组件，通过设置导入，或解压完整内容到：" + pluginFolder + "。若本次进程已使用 PaddleOCR，请先切换 Windows OCR 并重启，再升级。";

    private IOcrEngine Active()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (engineId() != "paddle") return windows;
            var manager = models();
            var selectedPreset = presetId();
            var key = manager.RootFolder + "\0" + selectedPreset;
            if (instances.TryGetValue(key, out var instance)) return instance;
            instance = LoadPlugin().Create(manager.RootFolder, selectedPreset);
            instances.Add(key, instance);
            return instance;
        }
    }
    private IOcrEnginePlugin LoadPlugin()
    {
        if (plugin is not null) return plugin;
        var candidate = new OcrPluginLoader(pluginFolder);
        try { plugin = candidate.Load(); loader = candidate; return plugin; }
        catch { candidate.Dispose(); throw; }
    }
    private static bool IsPluginError(Exception exception) => exception is IOException or UnauthorizedAccessException or BadImageFormatException or TypeLoadException or ReflectionTypeLoadException or InvalidOperationException or NotSupportedException or ArgumentException or DllNotFoundException or TargetInvocationException;
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var engine in instances.Values.OfType<IDisposable>()) engine.Dispose();
            instances.Clear(); plugin = null; loader?.Dispose(); loader = null;
        }
    }

}
