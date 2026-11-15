using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace SnapTrans.Core.Ocr;

/// <summary>Load the optional OCR component with isolated managed/native dependencies and shared Core/Skia contracts.</summary>
public sealed class OcrPluginLoader(string pluginFolder) : IDisposable
{
    private PluginLoadContext? context;
    private IOcrEnginePlugin? plugin;
    /// <summary>Inspect files and metadata only. This never loads an assembly or initializes ONNX.</summary>
    public OcrPluginProbeResult ProbeAvailability()
    {
        var folder = Path.GetFullPath(pluginFolder);
        try
        {
            var component = Path.Combine(folder, "SnapTrans.Ocr.Paddle.dll");
            var manifest = Path.Combine(folder, "SnapTrans.Ocr.Paddle.deps.json");
            if (!File.Exists(component) || !File.Exists(manifest)) throw new FileNotFoundException("组件 DLL 或依赖清单缺失。");
            if (new FileInfo(manifest).Length > 2 * 1024 * 1024) throw new InvalidDataException("组件依赖清单过大。");
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            string targetName = document.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString() ?? throw new InvalidDataException("组件运行平台无效。");
            string rid = CurrentRuntime();
            int slash = targetName.LastIndexOf('/');
            if (slash >= 0 && !targetName[(slash + 1)..].Equals(rid, StringComparison.OrdinalIgnoreCase))
                throw new BadImageFormatException("组件运行平台不匹配，请安装 " + rid + " 组件。");
            var target = document.RootElement.GetProperty("targets").GetProperty(targetName);
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SnapTrans.Ocr.Paddle.dll", "RapidOcrNet.dll", "Microsoft.ML.OnnxRuntime.dll", "Clipper2Lib.dll", "System.Numerics.Tensors.dll" };
            foreach (var library in target.EnumerateObject())
            {
                if (library.Value.TryGetProperty("runtime", out var runtime))
                    foreach (var asset in runtime.EnumerateObject())
                        if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) required.Add(Path.GetFileName(asset.Name));
                if (library.Value.TryGetProperty("native", out var nativeAssets))
                    foreach (var asset in nativeAssets.EnumerateObject()) CheckNativeAsset(asset.Name);
                if (library.Value.TryGetProperty("runtimeTargets", out var runtimeAssets))
                    foreach (var asset in runtimeAssets.EnumerateObject())
                        if (asset.Value.GetProperty("rid").GetString() == rid && asset.Value.GetProperty("assetType").GetString() == "native") CheckNativeAsset(asset.Name);
            }
            required.Remove("SnapTrans.Core.dll"); required.Remove("SkiaSharp.dll"); // Shared with the host.
            foreach (var name in required)
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path)) throw new FileNotFoundException("组件依赖缺失：" + name);
                using var file = File.OpenRead(path);
                using var image = new PEReader(file);
                if (!image.HasMetadata || image.PEHeaders.CorHeader is null) throw new BadImageFormatException("组件托管文件无效：" + name);
                if (name == "SnapTrans.Ocr.Paddle.dll" && !HasPluginContract(image.GetMetadataReader()))
                    throw new BadImageFormatException("组件未声明兼容的 OCR 插件入口。");
            }
            string nativeName = OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";
            string? native = new[] { Path.Combine(folder, nativeName), Path.Combine(folder, "runtimes", rid, "native", nativeName) }.FirstOrDefault(File.Exists);
            if (native is null) throw new FileNotFoundException("ONNX 原生运行时缺失，请安装完整的 " + rid + " 组件。");
            VerifyWindowsMachine(native, rid);
            return new(true, "组件文件与依赖检查通过（尚未加载）。", folder);

            void CheckNativeAsset(string asset)
            {
                // Import libraries (.lib) are build inputs and intentionally removed from release ZIPs.
                if (!(asset.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || asset.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || asset.Contains(".so", StringComparison.Ordinal))) return;
                string name = Path.GetFileName(asset);
                if (!File.Exists(Path.Combine(folder, name)) && !File.Exists(Path.Combine(folder, "runtimes", rid, "native", name)))
                    throw new FileNotFoundException("组件原生依赖缺失：" + name);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return new(false, "组件检查失败：" + exception.Message, folder); }
    }

    private static bool HasPluginContract(MetadataReader metadata)
    {
        foreach (var handle in metadata.TypeDefinitions)
            foreach (var implementation in metadata.GetTypeDefinition(handle).GetInterfaceImplementations())
            {
                var type = metadata.GetInterfaceImplementation(implementation).Interface;
                if (type.Kind != HandleKind.TypeReference) continue;
                var reference = metadata.GetTypeReference((TypeReferenceHandle)type);
                if (metadata.GetString(reference.Name) == "IOcrEnginePlugin" && metadata.GetString(reference.Namespace) == "SnapTrans.Core.Ocr") return true;
            }
        return false;
    }
    private static string CurrentRuntime() => (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsMacOS() ? "osx-" : "linux-") + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    private static void VerifyWindowsMachine(string path, string rid)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var file = File.OpenRead(path);
        using var image = new PEReader(file);
        var expected = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => Machine.Amd64, Architecture.Arm64 => Machine.Arm64, _ => Machine.Unknown };
        if (expected == Machine.Unknown || image.PEHeaders.CoffHeader.Machine != expected)
            throw new BadImageFormatException("PaddleOCR 原生组件架构与当前进程不匹配，请安装 " + rid + " 组件。");
    }
    public IOcrEnginePlugin Load()
    {
        if (plugin is not null) return plugin;
        var probe = ProbeAvailability();
        if (!probe.IsAvailable) throw new InvalidDataException(probe.Message + " 路径：" + probe.Folder);
        var path = Path.GetFullPath(Path.Combine(pluginFolder, "SnapTrans.Ocr.Paddle.dll"));
        if (!File.Exists(path)) throw new FileNotFoundException("PaddleOCR 可选组件未安装。", path);
        var candidate = new PluginLoadContext(path);
        try
        {
            candidate.VerifyNativeRuntime();
            var assembly = candidate.LoadFromAssemblyPath(path);
            var type = assembly.GetTypes().SingleOrDefault(type => !type.IsAbstract && typeof(IOcrEnginePlugin).IsAssignableFrom(type))
                ?? throw new InvalidDataException("OCR 组件缺少兼容的插件入口。");
            var created = (IOcrEnginePlugin?)Activator.CreateInstance(type) ?? throw new InvalidDataException("OCR 插件无法创建。");
            if (created.Id != "paddle") throw new InvalidDataException("OCR 插件标识不匹配。");
            context = candidate;
            plugin = created;
            return created;
        }
        catch { candidate.Unload(); throw; }
    }
    public void Dispose() { plugin = null; context?.Unload(); context = null; }

    private sealed class PluginLoadContext(string componentPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(componentPath);
        internal void VerifyNativeRuntime()
        {
            string name = OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";
            string rid = (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsMacOS() ? "osx-" : "linux-") + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string folder = Path.GetDirectoryName(componentPath)!;
            string? resolved = resolver.ResolveUnmanagedDllToPath("onnxruntime");
            var path = new[] { resolved, Path.Combine(folder, name), Path.Combine(folder, "runtimes", rid, "native", name) }.FirstOrDefault(path => path is not null && File.Exists(path))
                ?? throw new FileNotFoundException("PaddleOCR ONNX 运行时缺失，或组件架构与当前进程不一致。");
            VerifyWindowsMachine(path, rid);
        }
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "SnapTrans.Core" or "SkiaSharp") return null;
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}

public sealed record OcrPluginProbeResult(bool IsAvailable, string Message, string Folder);
