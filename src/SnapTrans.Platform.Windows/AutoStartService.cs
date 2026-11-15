using System.IO;
using Microsoft.Win32;

namespace SnapTrans.Platform.Windows;

public sealed class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnapTrans";
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }
    public void SetEnabled(bool enabled, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }
        var executable = Path.GetFullPath(exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("找不到程序路径。"));
        if (executable.Contains('"')) throw new ArgumentException("程序路径无效。", nameof(exePath));
        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}
