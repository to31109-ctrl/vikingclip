namespace VikingClip.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,     // MOD_ALT
    Control = 0x0002, // MOD_CONTROL
    Shift = 0x0004,   // MOD_SHIFT
    Win = 0x0008,     // MOD_WIN
}

/// <summary>A global hotkey: modifiers + Win32 virtual-key code. Serialized as text like "Alt+K".</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly Hotkey None = new(HotkeyModifiers.None, 0);
    public bool IsBound => VirtualKey != 0;

    public override string ToString()
    {
        if (!IsBound) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var mods = HotkeyModifiers.None;
        var key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= HotkeyModifiers.Control; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win": case "windows": mods |= HotkeyModifiers.Win; break;
                default: key = KeyCode(raw); break;
            }
        }
        return key == 0 ? None : new Hotkey(mods, key);
    }

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Escape"] = 0x1B, ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Pause"] = 0x13, ["PrintScreen"] = 0x2C,
        ["ScrollLock"] = 0x91, ["NumLock"] = 0x90, ["CapsLock"] = 0x14,
        ["Num0"] = 0x60, ["Num1"] = 0x61, ["Num2"] = 0x62, ["Num3"] = 0x63, ["Num4"] = 0x64,
        ["Num5"] = 0x65, ["Num6"] = 0x66, ["Num7"] = 0x67, ["Num8"] = 0x68, ["Num9"] = 0x69,
        ["NumMultiply"] = 0x6A, ["NumAdd"] = 0x6B, ["NumSubtract"] = 0x6D, ["NumDecimal"] = 0x6E, ["NumDivide"] = 0x6F,
        ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, [";"] = 0xBA,
        ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
    };

    private static int KeyCode(string name)
    {
        if (NamedKeys.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (name.Length is 2 or 3 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        return 0;
    }

    public static string KeyName(int vk)
    {
        foreach (var kv in NamedKeys) if (kv.Value == vk) return kv.Key;
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        return "0x" + vk.ToString("X2");
    }
}
