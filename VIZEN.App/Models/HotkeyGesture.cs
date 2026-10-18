using System.Windows.Input;

namespace VIZEN.App.Models;

/// <summary>
/// A global shortcut such as "Ctrl + Shift + F9": at least one modifier plus a non-modifier key.
/// Stored as text in settings and schedules.
/// </summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    // RegisterHotKey modifier flags.
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private static readonly Key[] ModifierOnlyKeys =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt,
        Key.LWin, Key.RWin, Key.System, Key.None,
    ];

    /// <summary>A modifier is required so the shortcut does not swallow normal typing or game keys.</summary>
    public bool IsValid => Modifiers != ModifierKeys.None && !ModifierOnlyKeys.Contains(Key);

    public uint Win32Modifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0)
        | (Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
        | (Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0)
        | (Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyText(Key));
        return string.Join(" + ", parts);
    }

    /// <summary>Parses text produced by <see cref="ToString"/> (case-insensitive, spaces optional).</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (key is not null || ParseKey(raw) is not { } parsed)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.Value);
        return gesture.IsValid;
    }

    private static string KeyText(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
        _ => key.ToString(),
    };

    private static Key? ParseKey(string text)
    {
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            return Key.D0 + (text[0] - '0');
        }

        if (text.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && text.Length == 4 && char.IsDigit(text[3]))
        {
            return Key.NumPad0 + (text[3] - '0');
        }

        return Enum.TryParse<Key>(text, ignoreCase: true, out var key) ? key : null;
    }
}
