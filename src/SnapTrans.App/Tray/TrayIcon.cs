using SnapTrans.App.Controls;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Geometry;
using Forms = System.Windows.Forms;

namespace SnapTrans.App.Tray;

/// <summary>Commands and live state shown in the tray menu.</summary>
public sealed class TrayCommands
{
    public required Action CaptureText { get; init; }
    public required Action CaptureImage { get; init; }
    public required Action RepeatLast { get; init; }
    public required Action CaptureWord { get; init; }
    public required Action Settings { get; init; }
    public required Action OpenPictures { get; init; }
    public required Action<bool> Pause { get; init; }
    public required Action PixelTest { get; init; }
    public required Action About { get; init; }
    public required Action Exit { get; init; }
    /// <summary>Current hotkey text for the capture commands; empty when unbound.</summary>
    public required Func<(string Floating, string Image, string Repeat, string Word)> Hotkeys { get; init; }
}

public sealed class TrayIcon : INotifier, IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly TrayCommands _commands;
    private TrayMenuWindow? _menu;
    private bool _paused;

    public TrayIcon(TrayCommands commands)
    {
        _commands = commands;
        _icon = new Forms.NotifyIcon { Text = "屏译 SnapTrans", Icon = CreateIcon(), Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) commands.CaptureText();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
    }

    /// <summary>Opens the themed menu at the cursor; the classic menu is the fallback if it cannot be shown.</summary>
    public void ShowMenu()
    {
        var items = Items();
        try
        {
            _menu?.Dismiss();
            var cursor = Forms.Cursor.Position;
            var menu = new TrayMenuWindow(items, new PixelPoint(cursor.X, cursor.Y));
            menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };
            _menu = menu;
            menu.Show();
        }
        catch (Exception e)
        {
            Serilog.Log.Warning("Tray menu failed {ErrorType} {Stack}", e.GetType().Name, e.StackTrace);
            ShowClassicMenu(items);
        }
    }

    private static void ShowClassicMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var strip = new Forms.ContextMenuStrip();
        foreach (var item in items)
        {
            if (item.Text is null) { strip.Items.Add(new Forms.ToolStripSeparator()); continue; }
            var action = item.Invoke;
            strip.Items.Add(new Forms.ToolStripMenuItem(item.Text, null, (_, _) => action?.Invoke()) { ShortcutKeyDisplayString = item.Shortcut, Checked = item.Checked });
        }
        strip.Closed += (_, _) => strip.BeginInvoke(strip.Dispose);
        strip.Show(Forms.Cursor.Position);
    }

    private IReadOnlyList<TrayMenuItem> Items()
    {
        var hotkeys = _commands.Hotkeys();
        return
        [
            new("截图翻译", Ui.Glyph.Globe, _commands.CaptureText, hotkeys.Floating),
            new("截图译图", Ui.Glyph.Picture, _commands.CaptureImage, hotkeys.Image),
            new("重复上次区域", Ui.Glyph.Repeat, _commands.RepeatLast, hotkeys.Repeat),
            new("框选查词", Ui.Glyph.Dictionary, _commands.CaptureWord, hotkeys.Word),
            TrayMenuItem.Separator,
            new("设置", Ui.Glyph.Settings, _commands.Settings),
            new("译图文件夹", Ui.Glyph.Folder, _commands.OpenPictures),
            new("暂停热键", Ui.Glyph.Pause, () => { _paused = !_paused; _commands.Pause(_paused); }, Checked: _paused),
            new("像素测试图", Ui.Glyph.Grid, _commands.PixelTest),
            new("关于", Ui.Glyph.Info, _commands.About),
            TrayMenuItem.Separator,
            new("退出", Ui.Glyph.Exit, _commands.Exit)
        ];
    }

    private static System.Drawing.Icon CreateIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("SnapTrans.Icon")
            ?? throw new InvalidOperationException("未找到应用图标资源。");
        using var source = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)source.Clone();
    }
    public void Info(string message) => _icon.ShowBalloonTip(3500, "屏译 SnapTrans", message, Forms.ToolTipIcon.Info);
    public void Error(string message, Exception? exception = null) => _icon.ShowBalloonTip(6000, "屏译 SnapTrans", message, Forms.ToolTipIcon.Warning);
    public void Dispose() { _menu?.Dismiss(); _icon.Visible = false; _icon.Icon?.Dispose(); _icon.Dispose(); }
}
