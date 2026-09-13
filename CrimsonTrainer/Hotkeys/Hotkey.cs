using System.Text;
using System.Windows.Input;

namespace CrimsonTrainer.Hotkeys;

/// <summary>A global key combination, e.g. Ctrl+Shift+F5.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        Key.Decimal => "Num .",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemTilde => "`",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        _ => key.ToString(),
    };

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('|');
        if (parts.Length != 2 || !Enum.TryParse(parts[0], out ModifierKeys mods) || !Enum.TryParse(parts[1], out Key key)) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>Stable form used in settings.json.</summary>
    public string Serialize() => $"{Modifiers}|{Key}";

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
