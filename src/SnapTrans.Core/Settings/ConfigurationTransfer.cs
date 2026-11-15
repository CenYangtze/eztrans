using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace SnapTrans.Core.Settings;

/// <summary>Imports and exports an independent, validated, credential-free settings snapshot.</summary>
public static class ConfigurationTransfer
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    { "apikeyprotected", "apikey", "key", "authorization", "password", "passwd", "secret", "token", "accesstoken", "refreshtoken", "clientsecret", "credential", "credentials" };

    public static async Task ExportAsync(AppSettings settings, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var node = JsonSerializer.SerializeToNode(settings, SettingsStore.JsonOptions)?.AsObject() ?? throw new JsonException("Settings are empty.");
        Sanitize(node);
        var sanitized = Deserialize(node);
        Validate(sanitized);
        // Serialize the sanitized tree, not the source settings. Key properties stay absent.
        string json = node.ToJsonString(SettingsStore.JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("配置文件不能超过2 MB。");
        string path = Path.GetFullPath(filePath);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, json, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<AppSettings> ImportAsync(string filePath, CancellationToken ct = default)
    {
        string path = Path.GetFullPath(filePath);
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("配置文件不能超过2 MB。");
        string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("配置必须为 JSON 对象。");
        SettingsMigrator.Migrate(node);
        Sanitize(node);
        var imported = Deserialize(node); Validate(imported);
        return imported;
    }
    /// <summary>Apply an imported snapshot while keeping the DI-owned root object alive.</summary>
    public static void CopyInto(AppSettings target, AppSettings imported)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(imported);
        var node = JsonSerializer.SerializeToNode(imported, SettingsStore.JsonOptions)?.AsObject() ?? throw new JsonException("配置为空。");
        SettingsMigrator.Migrate(node); Sanitize(node);
        var copy = Deserialize(node); Validate(copy);
        // All validation and copying finishes before the first mutation. Credentials
        // are never merged by profile id: an imported id may point at a different host.
        target.Version = copy.Version;
        target.General = copy.General; target.Hotkeys = copy.Hotkeys; target.Translation = copy.Translation;
        target.Profiles = copy.Profiles; target.Ocr = copy.Ocr; target.ImageTranslate = copy.ImageTranslate;
        target.Floating = copy.Floating; target.Network = copy.Network; target.Capture = copy.Capture;
        target.Cache = copy.Cache; target.History = copy.History; target.Updates = copy.Updates; target.Dictionary = copy.Dictionary;
    }
    private static AppSettings Deserialize(JsonObject node) => node.Deserialize<AppSettings>(SettingsStore.JsonOptions) ?? throw new JsonException("配置为空。");
    private static void Validate(AppSettings settings)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("；", errors));
    }
    private static void Sanitize(JsonObject root)
    {
        RemoveSecrets(root);
        if (root["profiles"] is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                // Free-form extra parameters may contain arbitrarily named credentials.
                // They are intentionally omitted instead of guessing whether their values are secrets.
                profile["extraBody"] = new JsonObject();
                if (profile["baseUrl"] is JsonValue url && url.TryGetValue<string>(out var text)) profile["baseUrl"] = SanitizeUrl(text);
            }
        }
        if (root["network"] is JsonObject network && network["customProxyUrl"] is JsonValue proxy && proxy.TryGetValue<string>(out var proxyUrl)) network["customProxyUrl"] = SanitizeUrl(proxyUrl);
        if (root["general"] is JsonObject general) { general["cloudPrivacyNoticeAccepted"] = false; general["onboardingCompleted"] = false; }
    }
    private static void RemoveSecrets(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(p => p.Key).ToArray())
            {
                string normalized = key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
                if (SensitiveNames.Contains(normalized)) { obj.Remove(key); continue; }
                if (obj[key] is { } child) RemoveSecrets(child);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) if (child is not null) RemoveSecrets(child);
    }
    private static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Fragment = "" };
        builder.Query = string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(part =>
        {
            string name = Uri.UnescapeDataString(part.Split('=', 2)[0]).Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
            return !SensitiveNames.Contains(name);
        }));
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
