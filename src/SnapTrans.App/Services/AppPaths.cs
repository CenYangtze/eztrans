namespace SnapTrans.App.Services;

public static class AppPaths
{
    public static SnapTrans.Core.Settings.SettingsPaths Paths { get; } = SnapTrans.Core.Settings.SettingsPaths.Resolve(
        AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static bool IsPortable => Paths.IsPortable;
    public static string SettingsFolder => Paths.SettingsFolder;
    public static string LocalFolder => Paths.LocalFolder;
    public static string LogFolder => Path.Combine(LocalFolder, "logs");
    public static string DebugFolder => Path.Combine(LocalFolder, "debug");
    public static string ModelFolder => Paths.ModelFolder;
    public static string HistoryFile => Paths.HistoryFile;
    public static string PluginFolder => Path.Combine(LocalFolder, "plugins", "paddle");
}
