using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SnapTrans.Ocr.Paddle.Interop;

internal static class TelemetryEnvironment
{
    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnvironment([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

    internal static void DisableOnnxTelemetry()
    {
        Environment.SetEnvironmentVariable("ORT_DISABLE_TELEMETRY", "1");
        // On Unix, .NET's managed environment snapshot and libc's getenv() are separate.
        // ORT reads libc's process environment while loading its native telemetry provider.
        if (!OperatingSystem.IsWindows() && SetEnvironment("ORT_DISABLE_TELEMETRY", "1", 1) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法关闭 ONNX 遥测，已停止加载 OCR 组件。");
    }
}
