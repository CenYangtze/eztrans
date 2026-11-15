using SnapTrans.Core.Caching;
using SnapTrans.Core.Data;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;

namespace SnapTrans.App.Windows;

/// <summary>Optional application services for advanced settings. Windows only owns display and input.</summary>
public sealed class SettingsFeatureServices
{
    public Action<bool>? HotkeyRecordingChanged { get; init; }
    public ITranslationHistory? History { get; init; }
    public ITranslationCache? Cache { get; init; }
    public Func<TranslationCacheStatistics>? CacheStatistics { get; init; }
    public Func<OcrModelManager>? GetOcrModels { get; init; }
    public SettingsPaths? StoragePaths { get; init; }
    public Func<AppSettings, CancellationToken, Task>? ApplyImportedSettingsAsync { get; init; }
    public Func<CancellationToken, Task<string>>? CheckForUpdatesAsync { get; init; }
    public Func<IProgress<int>?, CancellationToken, Task<string>>? InstallUpdateAsync { get; init; }
    public Func<bool>? HasAvailableUpdate { get; init; }
    public Func<bool>? IsPaddlePluginAvailable { get; init; }
    /// <summary>One-click setup for a model preset: component download, install, model download, enable.</summary>
    public Func<string, IProgress<SnapTrans.App.Services.PaddleSetupProgress>?, CancellationToken, Task<string>>? SetupPaddleAsync { get; init; }
    public Func<CancellationToken, Task<string>>? InstallPaddleFromFileAsync { get; init; }
}
