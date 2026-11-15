using System.Windows;
using System.Windows.Input;
using SnapTrans.Core.Hotkeys;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTrans.App.Controls;

/// <summary>Records a complete global gesture without installing a keyboard hook.</summary>
public sealed class HotkeyRecorder : TextBox
{
    public event Action<string>? GestureChanged;
    public string Gesture
    {
        get => Text;
        set => Text = value;
    }

    public HotkeyRecorder(string gesture)
    {
        Text = gesture;
        IsReadOnly = true;
        ToolTip = "点击后按下新快捷键";
        Tag = "未设置";
        MinWidth = 160;
        Cursor = Cursors.Hand;
        TextAlignment = TextAlignment.Center;
        SetResourceReference(StyleProperty, typeof(TextBox));
        SetResourceReference(FontFamilyProperty, "Font.Mono");
        PreviewKeyDown += Record;
    }

    private void Record(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && modifiers == ModifierKeys.None) { e.Handled = false; return; }
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            Key.Back => "Backspace",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            _ => key.ToString()
        });
        var gesture = string.Join("+", parts);
        try
        {
            var parsed = HotkeyGesture.Parse(gesture);
            Gesture = parsed.ToString();
            ToolTip = "点击后按下新快捷键";
            GestureChanged?.Invoke(Gesture);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            ToolTip = "无效：字母和数字键需配合 Ctrl、Alt、Shift 或 Win";
        }
    }
}
