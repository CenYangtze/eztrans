using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SnapTrans.Core.Dictionaries;
using SnapTrans.Core.Hotkeys;

namespace SnapTrans.Core.Settings;

public sealed class SettingsStore(string filePath)
{
    private readonly SemaphoreSlim _gate = new(1,1);
    public string FilePath { get; } = Path.GetFullPath(filePath);
    public bool WasRecovered { get; private set; }
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, WriteIndented=true, PropertyNameCaseInsensitive=true, DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull };
    public async Task<AppSettings> LoadAsync(CancellationToken ct=default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WasRecovered=false;
            if(!File.Exists(FilePath))
            {
                var defaults=new AppSettings(); await SaveCoreAsync(defaults,ct).ConfigureAwait(false); return defaults;
            }
            try
            {
                var raw=await File.ReadAllTextAsync(FilePath,ct).ConfigureAwait(false);
                var node=JsonNode.Parse(raw) as JsonObject ?? throw new JsonException("Settings root must be an object.");
                SettingsMigrator.Migrate(node);
                var settings=node.Deserialize<AppSettings>(JsonOptions)??throw new JsonException("Empty settings.");
                if(SettingsValidator.Validate(settings).Count>0) throw new JsonException("Settings validation failed.");
                return settings;
            }
            catch(Exception e) when(e is JsonException or FormatException or InvalidOperationException or ArgumentException)
            {
                string backup=Path.Combine(Path.GetDirectoryName(FilePath)!, $"settings.bad-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
                File.Move(FilePath,backup); WasRecovered=true;
                var defaults=new AppSettings(); await SaveCoreAsync(defaults,ct).ConfigureAwait(false); return defaults;
            }
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(AppSettings settings,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors=SettingsValidator.Validate(settings);
        if(errors.Count>0) throw new ArgumentException(string.Join(Environment.NewLine,errors),nameof(settings));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await SaveCoreAsync(settings,ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task SaveCoreAsync(AppSettings settings,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary=FilePath+".tmp";
        try
        {
            await using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream,settings,JsonOptions,ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            if(File.Exists(FilePath)) File.Replace(temporary,FilePath,null);
            else File.Move(temporary,FilePath);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class SettingsMigrator
{
    public const int CurrentVersion=3;
    public static JsonObject Migrate(JsonObject settings)
    {
        int version=settings["version"]?.GetValue<int>()??0;
        if(version>CurrentVersion) throw new NotSupportedException("此配置来自更高版本，请升级屏译后再打开。");
        if(version<0) throw new JsonException("Invalid settings version.");
        // Version 0 had the same optional fields but no explicit schema version.
        if(version==0) { settings["version"]=1; version=1; }
        // New optional sections deserialize with safe defaults. Existing profiles and
        // user-protected secrets remain unchanged during the v1 → v2 migration.
        if(version==1) { settings["version"]=2; version=2; }
        // v3 made image generation an explicit profile capability. Keep AI redraw working
        // for the profile a v2 user had already selected for it.
        if(version==2)
        {
            if(settings["profiles"] is JsonArray profiles && settings["imageTranslate"] is JsonObject image)
            {
                string? id=Text(image["aiProfileId"]);
                if(id is null && Text(image["engine"])=="ai" && settings["translation"] is JsonObject translation)
                    id=Text(translation["imageProfileId"])??Text(translation["activeProfileId"]);
                if(id is not null)
                    foreach(var profile in profiles.OfType<JsonObject>())
                        if(Text(profile["id"])==id) profile["supportsImageGeneration"]=true;
            }
            settings["version"]=3;
        }
        // The dictionary hotkey arrived after schema 3 without a version bump. Its default must not
        // collide with a gesture the user already assigned, or validation would reject the whole file.
        if(settings["hotkeys"] is JsonObject hotkeys && !hotkeys.ContainsKey("dictionary"))
        {
            string fallback=new HotkeySettings().Dictionary;
            foreach(string name in (string[])["translateFloating","translateImage","repeatLastRegion"])
                if(HotkeyGesture.TryParse(Text(hotkeys[name]),out var taken,out _) && taken==HotkeyGesture.Parse(fallback)) hotkeys["dictionary"]="";
        }
        return settings;
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;
}

public static class SettingsValidator
{
    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors=new List<string>();
        if(settings.General is null || settings.Hotkeys is null || settings.Translation is null || settings.Profiles is null || settings.Ocr is null || settings.ImageTranslate is null || settings.Floating is null || settings.Network is null || settings.Capture is null || settings.Cache is null || settings.History is null || settings.Updates is null || settings.Dictionary is null)
            return ["配置分区不能为空"];
        if(settings.Translation.Prompts is null || settings.Translation.Glossary is null || settings.ImageTranslate.FontMap is null || settings.Profiles.Any(p=>p is null))
            return ["配置对象不能为空"];
        if(settings.Version!=SettingsMigrator.CurrentVersion) errors.Add("不支持的配置版本");
        bool a=HotkeyGesture.TryParse(settings.Hotkeys.TranslateFloating,out var floating,out var e1),b=HotkeyGesture.TryParse(settings.Hotkeys.TranslateImage,out var image,out var e2);
        if(!a) errors.Add($"悬浮热键：{e1}");
        if(!b) errors.Add($"译图热键：{e2}");
        if(a&&b&&floating==image) errors.Add("两个快捷键不能重复");
        if(!string.IsNullOrWhiteSpace(settings.Hotkeys.RepeatLastRegion))
        {
            if(!HotkeyGesture.TryParse(settings.Hotkeys.RepeatLastRegion,out var repeat,out var repeatError)) errors.Add($"重复区域热键：{repeatError}");
            else if(repeat==floating || repeat==image) errors.Add("重复区域热键不能与翻译热键重复");
        }
        if(!string.IsNullOrWhiteSpace(settings.Hotkeys.Dictionary))
        {
            if(!HotkeyGesture.TryParse(settings.Hotkeys.Dictionary,out var dictionary,out var dictionaryError)) errors.Add($"查词热键：{dictionaryError}");
            else if(dictionary==floating || dictionary==image || (HotkeyGesture.TryParse(settings.Hotkeys.RepeatLastRegion,out var other,out _) && dictionary==other)) errors.Add("查词热键不能与其他热键重复");
        }
        if(settings.Dictionary.Mode is not ("cursor" or "capture")) errors.Add("取词方式无效");
        if(!DictionarySources.IsValid(settings.Dictionary.Source)) errors.Add("词典来源无效");
        if(settings.General.Theme is not ("system" or "light" or "dark")) errors.Add("主题设置无效");
        if(settings.General.ConfirmMode is not ("releaseToRun" or "toolbarConfirm" or "confirm")) errors.Add("确认模式无效");
        if(settings.Translation.Pipeline is not ("auto" or "vision" or "ocr-text" or "ocrText" or "ocr")) errors.Add("翻译管线无效");
        if(string.IsNullOrWhiteSpace(settings.Translation.TargetLanguage)||string.IsNullOrWhiteSpace(settings.Translation.SecondaryLanguage)) errors.Add("目标语言与第二语言不能为空");
        if(settings.Profiles.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=settings.Profiles.Count) errors.Add("模型配置 ID 不能重复");
        foreach(var profile in settings.Profiles)
        {
            if(string.IsNullOrWhiteSpace(profile.Id)||string.IsNullOrWhiteSpace(profile.Name)) errors.Add("模型配置 ID 和名称不能为空");
            if(!Uri.TryCreate(profile.BaseUrl,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||!string.IsNullOrEmpty(uri.UserInfo)) errors.Add($"{profile.Name}：接口地址须为不含凭证的 http/https 地址");
            if(profile.Kind is not ("openai-compatible" or "anthropic")) errors.Add($"{profile.Name}：未知接口类型");
            if(profile.StructuredOutput is not ("none" or "json_object" or "json_schema")) errors.Add($"{profile.Name}：结构化输出配置无效");
            if(profile.TimeoutSeconds is <1 or >3600) errors.Add($"{profile.Name}：超时应为1–3600秒");
            if(profile.MaxOutputTokens is <1 or >1000000) errors.Add($"{profile.Name}：输出上限无效");
            if(profile.MaxTokensField is not ("max_tokens" or "max_completion_tokens")) errors.Add($"{profile.Name}：输出上限字段无效");
            if(profile.Temperature is <0 or >2) errors.Add($"{profile.Name}：温度应为0–2或留空");
            if(profile.ExtraBody is null) errors.Add($"{profile.Name}：额外参数必须是 JSON 对象");
            if(profile.MaxConcurrency is <1 or >16) errors.Add($"{profile.Name}：并发请求数应为1–16");
        }
        if(settings.Network.Proxy is not ("system" or "custom" or "none")) errors.Add("代理模式无效");
        if(settings.Network.Proxy=="custom" && (!Uri.TryCreate(settings.Network.CustomProxyUrl,UriKind.Absolute,out var proxy)||proxy.Scheme is not ("http" or "socks5"))) errors.Add("自定义代理必须使用 http:// 或 socks5:// 地址");
        if(settings.Floating.FontSize is <10 or >28) errors.Add("悬浮窗字号应为10–28");
        if(settings.ImageTranslate.MinFontPx is <1 or >72) errors.Add("译图最小字号应为1–72");
        if(settings.ImageTranslate.Engine is not ("standard" or "ai")) errors.Add("译图引擎无效");
        if(settings.ImageTranslate.AiProvider is not ("openai" or "gemini")) errors.Add("AI 译图服务类型无效");
        if(settings.Cache.MaxEntries is <1 or >10000 || settings.Cache.TtlMinutes is <1 or >1440) errors.Add("缓存容量应为1–10000条，有效期应为1–1440分钟");
        if(settings.History.Limit is <1 or >5000) errors.Add("历史记录上限应为1–5000条");
        if(!Uri.TryCreate(settings.Updates.FeedUrl,UriKind.Absolute,out var feed) || feed.Scheme!="https" || !string.IsNullOrEmpty(feed.UserInfo) || !string.IsNullOrEmpty(feed.Query) || !string.IsNullOrEmpty(feed.Fragment)) errors.Add("更新源必须是无凭证、查询参数和片段的 HTTPS 地址");
        errors.AddRange(GlossaryParser.Validate(settings.Translation.Glossary));
        if(string.IsNullOrWhiteSpace(settings.ImageTranslate.SaveFolder)) errors.Add("保存目录不能为空");
        if(string.IsNullOrWhiteSpace(settings.ImageTranslate.FileNamePattern)||settings.ImageTranslate.FileNamePattern.IndexOfAny(['/', '\\'])>=0) errors.Add("文件名模板不能包含路径");
        return errors;
    }
}
