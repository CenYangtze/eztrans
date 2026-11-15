using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Hotkeys;
using SnapTrans.Platform.Windows.Interop;

namespace SnapTrans.Platform.Windows;

public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly HwndSource source;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<HotkeyId, HotkeyGesture> registered = [];
    private bool disposed;
    public event EventHandler<HotkeyId>? Pressed;

    public HotkeyService()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        source = new HwndSource(new HwndSourceParameters("SnapTrans.Hotkeys")
        {
            ParentWindow = new nint(-3), WindowStyle = 0, Width = 0, Height = 0
        });
        source.AddHook(ProcessMessage);
    }

    public bool TryRegister(HotkeyId id, HotkeyGesture gesture, out string? error)
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (registered.TryGetValue(id, out var old) && old == gesture) { error = null; return true; }
        if (registered.Any(pair => pair.Key != id && pair.Value == gesture))
        { error = "此快捷键已用于屏译中的另一项操作。"; return false; }
        NativeMethods.UnregisterHotKey(source.Handle, (int)id);
        if (NativeMethods.RegisterHotKey(source.Handle, (int)id, gesture.Modifiers | 0x4000, gesture.VirtualKey))
        { registered[id] = gesture; error = null; return true; }
        var code = Marshal.GetLastWin32Error();
        registered.Remove(id);
        bool restored = old is null || NativeMethods.RegisterHotKey(source.Handle, (int)id, old.Modifiers | 0x4000, old.VirtualKey);
        if (old is not null && restored) registered[id] = old;
        error = code == 1409 ? "快捷键已被其他程序占用。" : $"无法注册快捷键（错误 {code}）。";
        if (!restored) error += " 原快捷键也已失效，请重新设置。";
        return false;
    }

    public void Unregister(HotkeyId id)
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        NativeMethods.UnregisterHotKey(source.Handle, (int)id);
        registered.Remove(id);
    }

    public void UnregisterAll()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        foreach (var id in registered.Keys.ToArray()) Unregister(id);
    }

    private nint ProcessMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && registered.ContainsKey((HotkeyId)(int)wParam))
        {
            handled = true;
            Pressed?.Invoke(this, (HotkeyId)(int)wParam);
        }
        return 0;
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        UnregisterAll();
        source.RemoveHook(ProcessMessage);
        source.Dispose();
        disposed = true;
    }
}
