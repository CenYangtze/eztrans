namespace SnapTrans.Core.Settings;

/// <summary>Storage selection is fixed at process startup; creating portable.txt never moves existing data.</summary>
public sealed record SettingsPaths(bool IsPortable, string SettingsFolder, string LocalFolder)
{
    public string SettingsFile => Path.Combine(SettingsFolder, "settings.json");
    public string HistoryFile => Path.Combine(LocalFolder, "history.json");
    public string LogFolder => Path.Combine(LocalFolder, "logs");
    public string DebugFolder => Path.Combine(LocalFolder, "debug");
    public string ModelFolder => Path.Combine(LocalFolder, "models");

    public static SettingsPaths Resolve(string executableDirectory, string roamingAppData, string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        string executable = Path.GetFullPath(executableDirectory);
        bool portable = File.Exists(Path.Combine(executable, "portable.txt"));
        if (portable) return new(true, executable, Path.Combine(executable, "data"));
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData); ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        return new(false, Path.Combine(Path.GetFullPath(roamingAppData), "SnapTrans"), Path.Combine(Path.GetFullPath(localAppData), "SnapTrans"));
    }
}
