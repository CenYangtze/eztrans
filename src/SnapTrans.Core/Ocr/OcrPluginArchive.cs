using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SnapTrans.Core.Ocr;

public sealed record OcrPluginInstallResult(string Runtime, string Version, int FileCount, long BytesInstalled,
    string? BackupPath = null);

public sealed class OcrPluginInstallException(string message, bool requiresRestart = false, Exception? innerException = null)
    : IOException(message, innerException)
{
    public bool RequiresRestart { get; } = requiresRestart;
}

public sealed class OcrPluginArchiveOptions
{
    public int MaxFiles { get; init; } = 2048;
    public long MaxFileBytes { get; init; } = 256L * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;
    public long MaxExistingBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxManifestBytes { get; init; } = 1024 * 1024;
    public double MaxCompressionRatio { get; init; } = 500;
}

/// <summary>
/// Installs a user-selected, trusted plugin ZIP without executing any of its contents.
/// Manifest hashes establish integrity, not publisher identity or a digital signature.
/// The previous directory is retained as a recoverable backup after an upgrade.
/// </summary>
public static partial class OcrPluginArchive
{
    private const string ManifestName = "plugin-manifest.json";
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9.-]{0,63}$")]
    private static partial Regex RuntimeRegex();
    [GeneratedRegex(@"^[a-fA-F0-9]{64}$")]
    private static partial Regex HashRegex();
    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedNameRegex();
    [GeneratedRegex(@"\.so(?:\.[0-9]+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex LinuxLibraryRegex();

    public static Task<OcrPluginInstallResult> InstallAsync(string zipPath, string destination, string expectedRuntime,
        CancellationToken ct = default) => InstallAsync(zipPath, destination, expectedRuntime, new OcrPluginArchiveOptions(), ct);

    public static async Task<OcrPluginInstallResult> InstallAsync(string zipPath, string destination, string expectedRuntime,
        OcrPluginArchiveOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (!RuntimeRegex().IsMatch(expectedRuntime)) throw new ArgumentException("无效的组件运行平台。", nameof(expectedRuntime));
        if (options.MaxFiles is < 1 or > 100000 || options.MaxFileBytes < 1 || options.MaxTotalBytes < 1 || options.MaxExistingBytes < 1 ||
            options.MaxManifestBytes < 1 || !double.IsFinite(options.MaxCompressionRatio) || options.MaxCompressionRatio < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("不能安装到文件系统根目录。", nameof(destination));
        if (string.IsNullOrEmpty(Path.GetFileName(target))) throw new ArgumentException("不能安装到文件系统根目录。", nameof(destination));
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            RejectReparseAncestors(target);
            if (File.Exists(target) && !Directory.Exists(target)) throw Fail("组件安装目标不是目录。");
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, "." + Path.GetFileName(target) + ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            await using var source = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var entries = ValidateArchiveEntries(archive, options);
            if (!entries.TryGetValue(ManifestName, out var manifestEntry)) throw Fail("组件缺少 plugin-manifest.json。");
            var manifestBytes = await ReadBoundedAsync(manifestEntry.Open(), options.MaxManifestBytes, ct).ConfigureAwait(false);
            var manifest = ParseManifest(manifestBytes, expectedRuntime, options);
            var payload = manifest.Files.ToDictionary(f => ValidatePath(f.Path), StringComparer.OrdinalIgnoreCase);
            if (entries.Count != payload.Count + 1 || payload.Keys.Any(path => !entries.ContainsKey(path)))
                throw Fail("ZIP 文件与组件清单不一致，存在未列出或缺少的文件。");

            long total = 0;
            foreach (var (path, file) in payload)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[path];
                if (entry.Length > options.MaxFileBytes || entry.Length > options.MaxTotalBytes - total)
                    throw Fail("组件解压大小超过安全预算。");
                var output = ResolveInside(staging, path);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var input = entry.Open();
                await using var destinationFile = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long written = 0;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (count == 0) break;
                    if (count > options.MaxFileBytes - written || count > options.MaxTotalBytes - total)
                        throw Fail("组件实际解压大小超过安全预算。");
                    written += count; total += count;
                    hash.AppendData(buffer, 0, count);
                    await destinationFile.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
                if (written != entry.Length || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                    throw Fail("组件文件 SHA-256 校验失败，请重新获取可信的组件包。");
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, ManifestName), manifestBytes, ct).ConfigureAwait(false);
            archive.Dispose();
            await source.DisposeAsync().ConfigureAwait(false);

            var existing = Directory.Exists(target) ? ScanExisting(target, options, ct) : new Dictionary<string, ExistingFile>(StringComparer.OrdinalIgnoreCase);
            PluginManifest? previous = null;
            if (existing.TryGetValue(ManifestName, out var previousManifest))
                previous = ParseManifest(await ReadExistingBoundedAsync(previousManifest.FullPath, options.MaxManifestBytes, ct).ConfigureAwait(false), expectedRuntime, options);
            var previouslyManaged = previous?.Files.ToDictionary(f => ValidatePath(f.Path), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, PluginFile>(StringComparer.OrdinalIgnoreCase);

            RejectLoadedModules(target);
            foreach (var (path, file) in existing)
            {
                ct.ThrowIfCancellationRequested();
                if (path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                if (previouslyManaged.TryGetValue(path, out var old))
                {
                    if (!await MatchesHashAsync(file.FullPath, old.Sha256, ct).ConfigureAwait(false))
                        throw Fail("已安装的组件文件被修改，已保留全部文件。请先备份并在空目录重新安装。");
                    continue; // All managed files are replaced or removed according to the new manifest.
                }
                if (payload.ContainsKey(path)) throw Fail("组件包与安装目录中的非托管文件冲突，已拒绝覆盖用户文件。");
                var copy = ResolveInside(staging, path);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                await CopyExistingAsync(file.FullPath, copy, ct).ConfigureAwait(false);
            }
            // Re-check the live tree before replacing it. A changed file must not silently
            // disappear behind the new version, and no symlink is followed during copying.
            var current = Directory.Exists(target) ? ScanExisting(target, options, ct) : new Dictionary<string, ExistingFile>(StringComparer.OrdinalIgnoreCase);
            if (existing.Count != current.Count || existing.Any(pair => !current.TryGetValue(pair.Key, out var now) || now.Length != pair.Value.Length || now.LastWriteUtc != pair.Value.LastWriteUtc))
                throw Fail("安装期间目录中的文件发生变化，请停止相关操作后重试。");
            RejectLoadedModules(target);
            RejectReparseAncestors(target);
            ct.ThrowIfCancellationRequested();
            string? backup = null;
            if (Directory.Exists(target))
            {
                backup = Path.Combine(parent, "." + Path.GetFileName(target) + ".previous-" + Guid.NewGuid().ToString("N"));
                try { Directory.Move(target, backup); }
                catch (IOException error) { throw new OcrPluginInstallException("组件目录正在使用，请退出并重启后再安装。", true, error); }
            }
            try { Directory.Move(staging, target); staging = null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (backup is not null && !Directory.Exists(target))
                {
                    try { Directory.Move(backup, target); }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                    { throw new OcrPluginInstallException("安装失败，原组件与用户文件完整保留在：" + backup, true, restoreError); }
                }
                throw new OcrPluginInstallException("无法替换组件目录，请关闭正在使用组件的程序后重启安装。", true, error);
            }
            return new OcrPluginInstallResult(manifest.Runtime, manifest.Version, payload.Count, total, backup);
        }
        catch (InvalidDataException error) { throw new OcrPluginInstallException("组件 ZIP 损坏或格式无效。", false, error); }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            InstallGate.Release();
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateArchiveEntries(ZipArchive archive, OcrPluginArchiveOptions options)
    {
        if (archive.Entries.Count > options.MaxFiles * 2 + 1) throw Fail("组件文件数量超过安全预算。");
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                unixType is not (0 or 0x8000 or 0x4000)) throw Fail("组件 ZIP 不能包含符号链接、重解析点或特殊文件。");
            bool directory = entry.FullName.EndsWith('/');
            var path = ValidatePath(directory ? entry.FullName[..^1] : entry.FullName);
            if (directory)
            {
                if (entry.Length != 0) throw Fail("组件目录条目格式无效。");
                continue;
            }
            if (unixType == 0x4000) throw Fail("组件目录条目格式无效。");
            if (entry.Length > options.MaxFileBytes || (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > options.MaxCompressionRatio)))
                throw Fail("组件文件大小或压缩比例超过安全预算。");
            if (!files.TryAdd(path, entry)) throw Fail("组件 ZIP 包含重复或大小写冲突的文件名。");
        }
        if (files.Count > options.MaxFiles + 1) throw Fail("组件文件数量超过安全预算。");
        foreach (var path in files.Keys)
        {
            int slash = path.IndexOf('/');
            while (slash >= 0)
            {
                if (files.ContainsKey(path[..slash])) throw Fail("组件文件与目录名称冲突。");
                slash = path.IndexOf('/', slash + 1);
            }
        }
        return files;
    }

    private static PluginManifest ParseManifest(byte[] bytes, string expectedRuntime, OcrPluginArchiveOptions options)
    {
        PluginManifest manifest;
        try { manifest = JsonSerializer.Deserialize<PluginManifest>(bytes, ManifestJson) ?? throw new JsonException(); }
        catch (JsonException error) { throw new OcrPluginInstallException("组件清单 JSON 无效。", false, error); }
        if (!string.Equals(manifest.Runtime, expectedRuntime, StringComparison.OrdinalIgnoreCase)) throw Fail("组件运行平台不匹配，请选择 " + expectedRuntime + " 组件。");
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 64 || manifest.Version.Any(char.IsControl)) throw Fail("组件版本号无效。");
        if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > options.MaxFiles) throw Fail("组件清单文件数量无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null) throw Fail("组件清单文件项无效。");
            var path = ValidatePath(file.Path);
            if (!paths.Add(path)) throw Fail("组件清单包含重复文件。");
            if (!HashRegex().IsMatch(file.Sha256 ?? "")) throw Fail("组件清单 SHA-256 格式无效。");
            if (!IsAllowedPayload(path, expectedRuntime)) throw Fail("组件包包含不允许安装的文件类型或配置文件。");
        }
        return manifest;
    }

    private static string ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.StartsWith('/') || path.Contains('\\') ||
            path.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw Fail("组件包含不安全的文件路径。");
        var segments = path.Split('/');
        if (segments.Any(s => s.Length == 0 || s.Length > 128 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || ReservedNameRegex().IsMatch(s)))
            throw Fail("组件包含不安全的文件路径。");
        return path;
    }

    private static bool IsAllowedPayload(string path, string runtime)
    {
        var parts = path.Split('/');
        if (path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 4 || !parts[1].Equals(runtime, StringComparison.OrdinalIgnoreCase) || !parts[2].Equals("native", StringComparison.OrdinalIgnoreCase)) return false;
            var extension = Path.GetExtension(path);
            return runtime.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                : runtime.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase)
                : LinuxLibraryRegex().IsMatch(path);
        }
        if (parts[0].Equals("models", StringComparison.OrdinalIgnoreCase)) return path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        if (path.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase)) return true;
        if (parts[0].Equals("licenses", StringComparison.OrdinalIgnoreCase)) return path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        return path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveInside(string root, string relative)
    {
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw Fail("组件路径超出目标目录。");
        return result;
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Fail("组件安装路径不能包含符号链接或重解析点。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static Dictionary<string, ExistingFile> ScanExisting(string root, OcrPluginArchiveOptions options, CancellationToken ct)
    {
        var files = new Dictionary<string, ExistingFile>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        long bytes = 0;
        int items = 0;
        while (pending.Count > 0)
        {
            foreach (var item in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (++items > options.MaxFiles * 2) throw Fail("已有目录条目超过保留预算，请备份后在空目录安装。");
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw Fail("已安装目录包含链接，已拒绝修改。");
                if (item is DirectoryInfo folder) { pending.Push(folder.FullName); continue; }
                if (item is not FileInfo file) throw Fail("已安装目录包含特殊文件，已拒绝修改。");
                if (file.Length > options.MaxExistingBytes - bytes) throw Fail("已有目录超过保留预算，请备份后在空目录安装。");
                bytes += file.Length;
                try { using var check = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException error) { throw new OcrPluginInstallException("组件文件正在使用，请退出并重启后再安装。", true, error); }
                var path = Path.GetRelativePath(root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                ValidatePath(path);
                if (!files.TryAdd(path, new ExistingFile(file.FullName, file.Length, file.LastWriteTimeUtc))) throw Fail("已有目录存在大小写冲突文件。");
            }
        }
        return files;
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Bundled host assemblies have an empty Location and are deliberately ignored; only external plugin assemblies loaded from a physical path can prevent replacing the plugin directory.")]
    private static void RejectLoadedModules(string root)
    {
        bool Inside(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;
            if (Inside(assembly.Location)) throw new OcrPluginInstallException("OCR 组件已被当前进程加载，请重启后再安装更新。", true);
        }
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
                if (Inside(module.FileName)) throw new OcrPluginInstallException("OCR 原生组件已加载，请重启后再安装更新。", true);
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (PlatformNotSupportedException) { }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken ct)
    {
        await using (source)
        {
            using var output = new MemoryStream();
            var buffer = new byte[Math.Min(65536, limit)];
            while (true)
            {
                int count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (count == 0) return output.ToArray();
                if (count > limit - output.Length) throw Fail("组件清单超过安全预算。");
                output.Write(buffer, 0, count);
            }
        }
    }
    private static Task<byte[]> ReadExistingBoundedAsync(string path, int limit, CancellationToken ct) =>
        ReadBoundedAsync(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous), limit, ct);
    private static async Task<bool> MatchesHashAsync(string path, string expected, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false), Convert.FromHexString(expected));
    }
    private static async Task CopyExistingAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }
    private static OcrPluginInstallException Fail(string message) => new(message);
    private sealed record ExistingFile(string FullPath, long Length, DateTime LastWriteUtc);
    private sealed class PluginManifest
    {
        public required string Runtime { get; init; }
        public required string Version { get; init; }
        public required List<PluginFile> Files { get; init; }
    }
    private sealed class PluginFile
    {
        public required string Path { get; init; }
        public required string Sha256 { get; init; }
    }
}
