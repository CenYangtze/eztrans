using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SnapTrans.Core.Ocr;

/// <summary>Download only reviewed HTTPS artifacts, verify exact size and SHA-256, then atomically install.</summary>
public sealed class OcrModelManager
{
    private readonly HttpClient client;
    private readonly IReadOnlyList<OcrModelPreset> presets;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    public string RootFolder { get; }
    public IReadOnlyList<OcrModelPreset> Presets => presets;

    public OcrModelManager(HttpClient client, string rootFolder, IReadOnlyList<OcrModelPreset>? presets = null)
    {
        this.client = client;
        RootFolder = Path.GetFullPath(rootFolder);
        this.presets = presets ?? OcrModelCatalog.Presets;
        foreach (var preset in this.presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id) || preset.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                throw new ArgumentException("模型预设标识无效。", nameof(presets));
            if (preset.Files.Select(file => file.Role).Distinct(StringComparer.Ordinal).Count() != preset.Files.Count
                || preset.Files.Select(file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != preset.Files.Count)
                throw new ArgumentException("模型文件不能重复。", nameof(presets));
            foreach (var file in preset.Files)
                if (Path.GetFileName(file.FileName) != file.FileName || file.FileName.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || file.FileName is "." or ".." || file.FileName.Length == 0
                    || file.DownloadUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(file.DownloadUri.UserInfo)
                    || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)) || file.SizeBytes < 1)
                    throw new ArgumentException("模型下载必须具有安全文件名、HTTPS 地址、大小及 SHA-256。", nameof(presets));
        }
    }

    public string GetPresetFolder(string presetId) => Path.Combine(RootFolder, Preset(presetId).Id);
    public OcrModelPaths GetPaths(string presetId)
    {
        var preset = Preset(presetId);
        string Role(string name) => Path.Combine(GetPresetFolder(presetId), preset.Files.Single(file => file.Role == name).FileName);
        return new(Role("detector"), Role("classifier"), Role("recognizer"), Role("dictionary"));
    }

    public async Task<OcrModelStatus> GetStatusAsync(string presetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var missing = new List<string>();
        var invalid = new List<string>();
        foreach (var file in Preset(presetId).Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(GetPresetFolder(presetId), file.FileName);
            if (!File.Exists(path)) missing.Add(file.FileName);
            else if (!await MatchesAsync(path, file, ct).ConfigureAwait(false)) invalid.Add(file.FileName);
        }
        if (invalid.Count > 0) return new(OcrModelState.Invalid, "模型校验失败，请重新下载；未加载未经验证的文件。", invalid.Concat(missing).ToArray());
        return missing.Count > 0 ? new(OcrModelState.Missing, "OCR 模型尚未下载完整。", missing) : new(OcrModelState.Ready, "模型与配套字典已通过 SHA-256 校验。", []);
    }

    public bool HasAllFiles(string presetId) => Preset(presetId).Files.All(file =>
    {
        var info = new FileInfo(Path.Combine(GetPresetFolder(presetId), file.FileName));
        return info.Exists && info.Length == file.SizeBytes;
    });

    public async Task DownloadAsync(string presetId, IProgress<OcrModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var preset = Preset(presetId);
        var gate = gates.GetOrAdd(presetId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(GetPresetFolder(presetId));
            long completed = 0;
            foreach (var file in preset.Files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.Combine(GetPresetFolder(presetId), file.FileName);
                if (File.Exists(destination) && await MatchesAsync(destination, file, ct).ConfigureAwait(false))
                { completed += file.SizeBytes; progress?.Report(new(file.FileName, completed, preset.DownloadBytes)); continue; }
                var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUri);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"OCR 模型下载失败（HTTP {(int)response.StatusCode}），请检查网络或下载域名访问权限。", null, response.StatusCode);
                    if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new InvalidDataException("模型下载重定向到了不安全地址。");
                    if (response.Content.Headers.ContentLength is long length && length != file.SizeBytes)
                        throw new InvalidDataException("模型大小与受信清单不一致，已停止下载。");
                    await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buffer = new byte[81920];
                        long received = 0;
                        while (true)
                        {
                            int count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                            if (count == 0) break;
                            received += count;
                            if (received > file.SizeBytes) throw new InvalidDataException("模型下载超过受信大小限制。");
                            hash.AppendData(buffer, 0, count);
                            await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                            progress?.Report(new(file.FileName, completed + received, preset.DownloadBytes));
                        }
                        if (received != file.SizeBytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("模型 SHA-256 校验失败，已删除未验证的下载；请稍后重试。");
                        await target.FlushAsync(ct).ConfigureAwait(false);
                    }
                    ct.ThrowIfCancellationRequested();
                    File.Move(partial, destination, overwrite: true);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
                completed += file.SizeBytes;
                progress?.Report(new(file.FileName, completed, preset.DownloadBytes));
            }
        }
        finally { gate.Release(); }
    }

    private OcrModelPreset Preset(string id) => presets.FirstOrDefault(preset => preset.Id == id) ?? throw new ArgumentException("未知 OCR 模型预设。", nameof(id));
    private static async Task<bool> MatchesAsync(string path, OcrModelFile file, CancellationToken ct)
    {
        if (new FileInfo(path).Length != file.SizeBytes) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
