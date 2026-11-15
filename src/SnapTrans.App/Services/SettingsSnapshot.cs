using System.Text.Json;
using SnapTrans.Core.Settings;

namespace SnapTrans.App.Services;
internal static class SettingsSnapshot
{
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SettingsStore.JsonOptions), SettingsStore.JsonOptions)
        ?? throw new InvalidOperationException("无法读取设置快照。");
}
