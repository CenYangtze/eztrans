using System.Text.Json.Nodes;
using SnapTrans.Core.Translation;

namespace SnapTrans.Core.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 3;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public List<Profile> Profiles { get; set; } = [ProfilePresets.Create("Ollama")];
    public OcrSettings Ocr { get; set; } = new();
    public ImageTranslateSettings ImageTranslate { get; set; } = new();
    public FloatingSettings Floating { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public DictionarySettings Dictionary { get; set; } = new();
}
/// <summary>Word lookup. These fields are optional additions to schema 3, so 1.0.1 can still read the file.</summary>
public sealed class DictionarySettings
{
    /// <summary>cursor: look up the word nearest the pointer; capture: draw a region around the word first.</summary>
    public string Mode { get; set; } = "cursor";
    /// <summary>auto, youdao or google.</summary>
    public string Source { get; set; } = "auto";
}
public sealed class GeneralSettings
{
    public bool DebugLogging { get; set; }
    public bool LaunchAtStartup { get; set; }
    public string ConfirmMode { get; set; } = "releaseToRun";
    public bool ExcludeOwnWindowsFromCapture { get; set; }
    public bool CloudPrivacyNoticeAccepted { get; set; }
    public bool OnboardingCompleted { get; set; }
    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";
}
public sealed class HotkeySettings
{
    public string TranslateFloating { get; set; } = "Alt+Q";
    public string TranslateImage { get; set; } = "Alt+Shift+Q";
    public string RepeatLastRegion { get; set; } = "";
    /// <summary>Empty disables the dictionary hotkey.</summary>
    public string Dictionary { get; set; } = "Alt+W";
}
public sealed class CaptureSettings
{
    public bool Magnifier { get; set; }
    public bool WindowSelection { get; set; }
    public bool UseGraphicsCaptureFallback { get; set; } = true;
}
public sealed class CacheSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 128;
    public int TtlMinutes { get; set; } = 30;
}
public sealed class HistorySettings
{
    public bool Enabled { get; set; }
    public int Limit { get; set; } = 200;
}
public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; }
    public string FeedUrl { get; set; } = "https://github.com/CenYangtze/eztrans";
    public bool IncludePrerelease { get; set; }
}
public sealed class TranslationSettings
{
    public string TargetLanguage { get; set; } = "zh-Hans";
    public string SecondaryLanguage { get; set; } = "en";
    public bool SmartSwap { get; set; } = true;
    public string Pipeline { get; set; } = "auto";
    public string ActiveProfileId { get; set; } = "ollama";
    public string? ImageProfileId { get; set; }
    public bool AttachImageInImageMode { get; set; } = true;
    public List<GlossaryEntry> Glossary { get; set; } = [];
    public PromptSettings Prompts { get; set; } = new();
}
public sealed class PromptSettings
{
    public string? Vision { get; set; }
    public string? Text { get; set; }
    public string? Segments { get; set; }
}
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新模型";
    public string Kind { get; set; } = "openai-compatible";
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string ApiKeyProtected { get; set; } = "";
    public string AuthHeader { get; set; } = "bearer";
    public string Model { get; set; } = "";
    public bool SupportsVision { get; set; }
    /// <summary>The model can create or edit images (text-to-image / image edit); required for AI redraw.</summary>
    public bool SupportsImageGeneration { get; set; }
    /// <summary>Parallel requests for segmented translation; null selects 1 for loopback hosts and 4 otherwise.</summary>
    public int? MaxConcurrency { get; set; }
    public string StructuredOutput { get; set; } = "none";
    public double? Temperature { get; set; }
    public int MaxOutputTokens { get; set; } = 4096;
    public string MaxTokensField { get; set; } = "max_tokens";
    public string? ImageDetail { get; set; }
    public string AnthropicVersion { get; set; } = "2026-10-21";
    public JsonObject ExtraBody { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 60;
}
public sealed class OcrSettings
{
    public string Engine { get; set; } = "windows";
    public string Language { get; set; } = "auto";
    public string Scale { get; set; } = "auto";
    public string ModelPreset { get; set; } = "latin-v5";
    public string? ModelFolder { get; set; }
}
public sealed class ImageTranslateSettings
{
    public bool AutoCopy { get; set; } = true;
    public bool AutoSave { get; set; }
    public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"SnapTrans");
    public string FileNamePattern { get; set; } = "SnapTrans_{yyyyMMdd_HHmmss}.png";
    public float MinFontPx { get; set; } = 9;
    public Dictionary<string,string> FontMap { get; set; } = new() { ["zh-Hans"]="Microsoft YaHei UI",["zh-Hant"]="Microsoft JhengHei UI",["ja"]="Yu Gothic UI",["ko"]="Malgun Gothic",["default"]="Segoe UI" };
    public string EraseMode { get; set; } = "auto";
    public bool DebugDump { get; set; }
    public string Engine { get; set; } = "standard";
    public string AiProvider { get; set; } = "openai";
    public string? AiProfileId { get; set; }
    public bool EnableInpaint { get; set; }
    public bool PreserveBold { get; set; }
    public bool PreserveOutline { get; set; }
}
public sealed class FloatingSettings
{
    public double FontSize { get; set; } = 14;
    public string? FontFamily { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool CloseOnDeactivate { get; set; }
    public bool SideBySide { get; set; }
}
public sealed class NetworkSettings
{
    public string Proxy { get; set; } = "system";
    public string? CustomProxyUrl { get; set; }
}

public static class ProfileRules
{
    public const int DefaultRemoteConcurrency = 4;
    /// <summary>Local servers usually serve one request at a time, so parallel batches only repeat prompt overhead.</summary>
    public static int Concurrency(Profile profile) => Math.Clamp(profile.MaxConcurrency
        ?? (Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback ? 1 : DefaultRemoteConcurrency), 1, 16);
}

/// <summary>AI redraw is offered only when a profile that can generate or edit images is selected for it.</summary>
public static class ImageGeneration
{
    /// <summary>The dedicated AI profile when one is chosen, otherwise the profile used for image translation.</summary>
    public static Profile? Resolve(AppSettings settings)
    {
        string? id = settings.ImageTranslate.AiProfileId ?? settings.Translation.ImageProfileId ?? settings.Translation.ActiveProfileId;
        var profile = settings.Profiles.FirstOrDefault(p => p.Id == id);
        return profile is { SupportsImageGeneration: true } && !string.IsNullOrWhiteSpace(profile.Model) ? profile : null;
    }
    public static bool IsAvailable(AppSettings settings) => Resolve(settings) is not null;
}

public static class ProfilePresets
{
    public static IReadOnlyList<string> Names { get; } = ["OpenAI","Anthropic Claude","Google Gemini","DeepSeek","阿里云百炼","智谱","月之暗面 Kimi","火山方舟","硅基流动","OpenRouter","Ollama","LM Studio"];
    public static Profile Create(string name) => name switch
    {
        "OpenAI" => Make(name,"https://api.openai.com/v1",vision:true,maxTokens:"max_completion_tokens"),
        "Anthropic Claude" => Make(name,"https://api.anthropic.com",vision:true,kind:"anthropic"),
        "Google Gemini" => Make(name,"https://generativelanguage.googleapis.com/v1beta/openai",vision:true),
        "DeepSeek" => Make(name,"https://api.deepseek.com"),
        "阿里云百炼" => Make(name,"https://dashscope.aliyuncs.com/compatible-mode/v1"),
        "智谱" => Make(name,"https://open.bigmodel.cn/api/paas/v4"),
        "月之暗面 Kimi" => Make(name,"https://api.moonshot.cn/v1"),
        "火山方舟" => Make(name,"https://ark.cn-beijing.volces.com/api/v3"),
        "硅基流动" => Make(name,"https://api.siliconflow.cn/v1"),
        "OpenRouter" => Make(name,"https://openrouter.ai/api/v1"),
        "LM Studio" => Make(name,"http://localhost:1234/v1"),
        _ => Make("Ollama","http://localhost:11434/v1",id:"ollama")
    };
    private static Profile Make(string name,string url,bool vision=false,string kind="openai-compatible",string maxTokens="max_tokens",string? id=null) => new() { Id=id??Guid.NewGuid().ToString("N"), Name=name, BaseUrl=url, SupportsVision=vision,Kind=kind,MaxTokensField=maxTokens };
}
