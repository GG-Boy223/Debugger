using System.Globalization;
using System.Windows.Input;

namespace DogeDebugger.Core.Shortcuts;

public readonly struct KeyChord : IEquatable<KeyChord>
{
    public KeyChord(Key key, ModifierKeys modifiers)
    {
        Key = key;
        Modifiers = modifiers & SupportedModifiers;
    }

    public static KeyChord None { get; } = new(Key.None, ModifierKeys.None);

    public Key Key { get; }

    public ModifierKeys Modifiers { get; }

    public bool IsEmpty => Key == Key.None;

    public bool HasControl => (Modifiers & ModifierKeys.Control) != 0;

    public bool HasShift => (Modifiers & ModifierKeys.Shift) != 0;

    public bool HasAlt => (Modifiers & ModifierKeys.Alt) != 0;

    private const ModifierKeys SupportedModifiers =
        ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift;

    public static KeyChord Create(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        return new KeyChord(NormalizeKey(key), modifiers);
    }

    public static KeyChord FromKeyEvent(KeyEventArgs eventArgs)
    {
        Key key = eventArgs.Key == Key.System
            ? eventArgs.SystemKey
            : eventArgs.Key;
        if (IsModifierKey(key))
        {
            return None;
        }

        ModifierKeys modifiers = Keyboard.Modifiers & SupportedModifiers;
        return Create(key, modifiers);
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ModifierKeys modifiers = ModifierKeys.None;
        string[] parts = text.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (int index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                default:
                    return false;
            }
        }

        if (!TryParseKey(parts[^1], out Key key))
        {
            return false;
        }

        chord = Create(key, modifiers);
        return true;
    }

    public static KeyChord Parse(string text)
    {
        return TryParse(text, out KeyChord chord)
            ? chord
            : throw new FormatException($"无法解析快捷键文本：\"{text}\"");
    }

    public bool Equals(KeyChord other)
    {
        return Key == other.Key && Modifiers == other.Modifiers;
    }

    public override bool Equals(object? obj)
    {
        return obj is KeyChord other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Key, Modifiers);
    }

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        List<string> parts = [];
        if (HasControl)
        {
            parts.Add("Ctrl");
        }

        if (HasShift)
        {
            parts.Add("Shift");
        }

        if (HasAlt)
        {
            parts.Add("Alt");
        }

        parts.Add(FormatKey(Key));
        return string.Join("+", parts);
    }

    public static bool operator ==(KeyChord left, KeyChord right) => left.Equals(right);

    public static bool operator !=(KeyChord left, KeyChord right) => !left.Equals(right);

    private static bool TryParseKey(string text, out Key key)
    {
        string value = text.Trim();
        if (value.Length == 1)
        {
            char character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z')
            {
                key = (Key)(character - 'A' + (int)Key.A);
                return true;
            }

            if (character is >= '0' and <= '9')
            {
                key = (Key)(character - '0' + (int)Key.D0);
                return true;
            }

            key = character switch
            {
                '*' => Key.Multiply,
                '+' => Key.OemPlus,
                '-' => Key.OemMinus,
                ';' => Key.OemSemicolon,
                ':' => Key.OemSemicolon,
                '/' => Key.OemQuestion,
                '.' => Key.OemPeriod,
                ',' => Key.OemComma,
                _ => Key.None
            };
            return key != Key.None;
        }

        string normalized = value.ToLowerInvariant() switch
        {
            "ins" => nameof(Key.Insert),
            "del" => nameof(Key.Delete),
            "return" => nameof(Key.Enter),
            "escape" => nameof(Key.Escape),
            "pgup" => nameof(Key.PageUp),
            "pgdn" => nameof(Key.PageDown),
            "back" => nameof(Key.Back),
            "spacebar" => nameof(Key.Space),
            "pause" => nameof(Key.Pause),
            _ => value
        };
        return Enum.TryParse(normalized, ignoreCase: true, out key);
    }

    private static Key NormalizeKey(Key key)
    {
        return key switch
        {
            Key.Return => Key.Enter,
            _ => key
        };
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftAlt or Key.RightAlt or
            Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin;
    }

    private static string FormatKey(Key key)
    {
        return key switch
        {
            Key.Multiply => "*",
            Key.OemPlus or Key.Add => "+",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemSemicolon => ";",
            Key.OemQuestion => "/",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.D4 or
            Key.D5 or Key.D6 or Key.D7 or Key.D8 or Key.D9 =>
                ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture),
            _ => key.ToString()
        };
    }
}
