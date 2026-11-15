using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SnapTrans.App.Controls;

/// <summary>
/// Publishes the Brush.* palette used by App.xaml styles and follows the Windows light/dark app setting.
/// Brushes are swapped in application resources, so every DynamicResource consumer updates in place.
/// </summary>
public static class SystemTheme
{
    private static bool _initialized;
    private static string _mode = "system";
    public static bool IsDark { get; private set; }

    /// <summary>"system" follows Windows; "light" and "dark" force a palette.</summary>
    public static void SetMode(string mode)
    {
        mode = mode is "light" or "dark" ? mode : "system";
        if (_mode == mode && _initialized) return;
        _mode = mode;
        if (_initialized) Apply(); else Initialize();
    }

    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("Brush.Background", "#F3F4F7", "#1B1D22"),
        ("Brush.Sidebar", "#ECEEF2", "#202329"),
        ("Brush.Card", "#FFFFFF", "#262930"),
        ("Brush.CardBorder", "#E3E6EC", "#30343C"),
        ("Brush.Popup", "#FFFFFF", "#2B2F37"),
        ("Brush.Field", "#FFFFFF", "#2D3139"),
        ("Brush.FieldBorder", "#D3D8E0", "#3D424D"),
        ("Brush.FieldBorderHover", "#A8B0BD", "#59606D"),
        ("Brush.Control", "#FFFFFF", "#30343C"),
        ("Brush.ControlBorder", "#D9DDE4", "#3D424C"),
        ("Brush.ControlHover", "#F0F2F5", "#383C45"),
        ("Brush.ControlPressed", "#E5E8ED", "#41464F"),
        ("Brush.Divider", "#E6E8ED", "#33373F"),
        ("Brush.Text", "#1B1F27", "#ECEEF2"),
        ("Brush.TextMuted", "#5D6573", "#A4ABB7"),
        ("Brush.TextSubtle", "#8C93A0", "#7A8290"),
        ("Brush.Accent", "#3461F0", "#5B7FF5"),
        ("Brush.AccentHover", "#2C55DA", "#7091F7"),
        ("Brush.AccentPressed", "#2648BD", "#4C6FE3"),
        ("Brush.AccentSoft", "#E7EDFE", "#2E3757"),
        ("Brush.OnAccent", "#FFFFFF", "#FFFFFF"),
        ("Brush.SwitchOff", "#FFFFFF", "#2D3139"),
        ("Brush.SwitchBorder", "#8C93A0", "#8C93A0"),
        ("Brush.SwitchKnob", "#5D6573", "#C3C8D1"),
        ("Brush.ScrollThumb", "#C3C8D1", "#4A505B"),
        ("Brush.Danger", "#C93838", "#FF7B72"),
        ("Brush.DangerSoft", "#FDECEC", "#3D2526"),
        ("Brush.Success", "#1D8A55", "#4CC38A"),
        ("Brush.SuccessSoft", "#E5F5EC", "#1F3A2C"),
        ("Brush.Warning", "#A86308", "#F0B04A"),
        ("Brush.WarningSoft", "#FDF3E2", "#3A3020"),
    ];

    /// <summary>Applies the palette and subscribes to theme changes once per process.</summary>
    public static void Initialize()
    {
        if (_initialized || Application.Current is null) return;
        _initialized = true;
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    /// <summary>Binds a window's surface to the palette and gives it a matching title bar.</summary>
    public static void Attach(Window window)
    {
        Initialize();
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Background");
        window.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
        window.SetResourceReference(Window.FontFamilyProperty, "Font.Ui");
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    private static void Apply()
    {
        bool dark = _mode == "dark";
        if (_mode == "system")
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch { dark = false; }
        }
        IsDark = dark;
        var resources = Application.Current.Resources;
        foreach (var (key, light, darkValue) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkValue : light));
            brush.Freeze();
            resources[key] = brush;
        }
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    private static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int enabled = IsDark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on current builds, 19 on early Windows 10 20H1 builds.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
