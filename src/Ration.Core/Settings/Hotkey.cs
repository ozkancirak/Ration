namespace Ration.Core.Settings;

/// <summary>
/// Genel kısayol: değiştirici bayrakları (Win32 MOD_* değerleri) ve sanal tuş kodu. Ayarda
/// "Ctrl+Alt+R" biçiminde durur. Yazı yazarken tetiklenmesin diye en az bir Ctrl/Alt/Win
/// değiştiricisi ve harf, rakam ya da F1-F24 tuşu ister.
/// </summary>
public readonly record struct Hotkey(int Modifiers, int VirtualKey)
{
    public const int Alt = 0x1;
    public const int Control = 0x2;
    public const int Shift = 0x4;
    public const int Win = 0x8;

    private const int FunctionKeyBase = 0x70; // VK_F1

    public static Hotkey Default { get; } = new(Control | Alt, 'R');

    public static bool TryCreate(int modifiers, int virtualKey, out Hotkey hotkey)
    {
        hotkey = default;
        if ((modifiers & ~(Alt | Control | Shift | Win)) != 0) return false;
        if ((modifiers & (Alt | Control | Win)) == 0) return false;
        if (!IsSupportedKey(virtualKey)) return false;

        hotkey = new Hotkey(modifiers, virtualKey);
        return true;
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        int modifiers = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            int flag = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => Control,
                "alt" => Alt,
                "shift" => Shift,
                "win" => Win,
                _ => 0,
            };
            if (flag == 0) return false;
            modifiers |= flag;
        }

        return TryParseKey(parts[^1], out var key) && TryCreate(modifiers, key, out hotkey);
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        if ((Modifiers & Win) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static bool IsSupportedKey(int key) =>
        key is >= 'A' and <= 'Z' or >= '0' and <= '9' or >= FunctionKeyBase and < FunctionKeyBase + 24;

    private static string KeyName(int key) =>
        key >= FunctionKeyBase ? $"F{key - FunctionKeyBase + 1}" : ((char)key).ToString();

    private static bool TryParseKey(string name, out int key)
    {
        key = 0;
        if (name.Length == 0) return false;
        if (name.Length == 1)
        {
            key = char.ToUpperInvariant(name[0]);
            return true;
        }

        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var number) && number is >= 1 and <= 24)
        {
            key = FunctionKeyBase + number - 1;
            return true;
        }

        return false;
    }
}
