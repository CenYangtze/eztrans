namespace SnapTrans.Core.Hotkeys;

/// <summary>A platform independent gesture using the documented Win32 modifier and virtual-key values.</summary>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public string Key => FormatKey(VirtualKey);
    public static HotkeyGesture Parse(string value)
    {
        if(!TryParse(value,out var gesture,out var error)) throw new FormatException(error);
        return gesture!;
    }
    public static bool TryParse(string? value, out HotkeyGesture? gesture, out string? error)
    {
        gesture=null; error=null;
        if(string.IsNullOrWhiteSpace(value)) { error="请输入快捷键"; return false; }
        var tokens=value.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        uint modifiers=0, key=0;
        foreach(var token in tokens)
        {
            uint modifier=token.ToUpperInvariant() switch { "ALT"=>1, "CTRL" or "CONTROL"=>2, "SHIFT"=>4, "WIN" or "WINDOWS"=>8, _=>0 };
            if(modifier!=0)
            {
                if((modifiers&modifier)!=0) { error="修饰键不能重复"; return false; }
                modifiers|=modifier; continue;
            }
            if(key!=0) { error="只能包含一个普通按键"; return false; }
            key=ParseKey(token);
            if(key==0) { error=$"不支持的按键：{token}"; return false; }
        }
        if(key==0 || (modifiers==0 && key is not (>=112 and <=135))) { error="除 F1–F24 外必须同时包含修饰键和普通按键"; return false; }
        gesture=new(modifiers,key); return true;
    }
    public override string ToString()
    {
        var parts=new List<string>();
        if((Modifiers&2)!=0) parts.Add("Ctrl");
        if((Modifiers&1)!=0) parts.Add("Alt");
        if((Modifiers&4)!=0) parts.Add("Shift");
        if((Modifiers&8)!=0) parts.Add("Win");
        parts.Add(Key); return string.Join('+',parts);
    }
    private static uint ParseKey(string value)
    {
        string key=value.ToUpperInvariant();
        if(key.Length==1 && char.IsAsciiLetterOrDigit(key[0])) return key[0];
        if(key.StartsWith('F') && uint.TryParse(key.AsSpan(1),out uint f) && f is >=1 and <=24) return 111+f;
        return key switch { "SPACE"=>32,"ENTER" or "RETURN"=>13,"TAB"=>9,"ESC" or "ESCAPE"=>27,"BACKSPACE"=>8,"DELETE"=>46,"INSERT"=>45,"HOME"=>36,"END"=>35,"PAGEUP"=>33,"PAGEDOWN"=>34,"LEFT"=>37,"UP"=>38,"RIGHT"=>39,"DOWN"=>40,"PRINTSCREEN"=>44,"OEMMINUS" or "MINUS"=>189,"OEMPLUS" or "PLUS"=>187,_=>0 };
    }
    private static string FormatKey(uint key) => key switch { >=65 and <=90 or >=48 and <=57 => ((char)key).ToString(), >=112 and <=135 => $"F{key-111}",32=>"Space",13=>"Enter",9=>"Tab",27=>"Esc",8=>"Backspace",46=>"Delete",45=>"Insert",36=>"Home",35=>"End",33=>"PageUp",34=>"PageDown",37=>"Left",38=>"Up",39=>"Right",40=>"Down",44=>"PrintScreen",189=>"Minus",187=>"Plus",_=>$"{key}" };
}
