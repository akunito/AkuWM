namespace AkuWM.Core.Input;

/// <summary>
/// A key combination as the configuration writes it: <c>Hyper+Shift+C</c>,
/// <c>Ctrl+Alt+C</c>, <c>Hyper+F9</c>.
/// </summary>
/// <remarks>
/// <c>Hyper</c> is this desk's Ctrl+Alt+Win, the modifier the sway setup
/// uses and the reason the chords cannot go through <c>RegisterHotKey</c>:
/// the keyboard's Office key already owns some of those combinations, so from
/// M3 AkuWM reads them off its own low-level hook. Parsing them is pure, and
/// is what the validator checks a shortcut with.
/// </remarks>
public readonly record struct Chord(KeyModifiers Modifiers, string Key)
{
    public static bool TryParse(string? text, out Chord chord, out string? error)
    {
        chord = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty";
            return false;
        }

        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty))
        {
            error = $"'{text}' has an empty part";
            return false;
        }

        KeyModifiers modifiers = KeyModifiers.None;
        string? key = null;

        foreach (string part in parts)
        {
            KeyModifiers? modifier = AsModifier(part);
            if (modifier is not null)
            {
                if (modifiers.HasFlag(modifier.Value))
                {
                    error = $"'{part}' is repeated";
                    return false;
                }

                modifiers |= modifier.Value;
                continue;
            }

            if (key is not null)
            {
                error = $"'{text}' names two keys ('{key}' and '{part}')";
                return false;
            }

            if (!IsKey(part))
            {
                error = $"'{part}' is not a key AkuWM knows";
                return false;
            }

            key = part.ToUpperInvariant();
        }

        if (key is null)
        {
            error = $"'{text}' is modifiers only";
            return false;
        }

        chord = new Chord(modifiers, key);
        return true;
    }

    private static KeyModifiers? AsModifier(string part) => part.ToLowerInvariant() switch
    {
        "hyper" => KeyModifiers.Hyper,
        "ctrl" or "control" => KeyModifiers.Ctrl,
        "alt" => KeyModifiers.Alt,
        "shift" => KeyModifiers.Shift,
        "win" or "super" or "meta" => KeyModifiers.Win,
        _ => null,
    };

    private static bool IsKey(string part)
    {
        // Any one printable character: the shifted ones too (`?` for the
        // right-hand focus, `:` for the move; the AutoHotkey binds them by
        // the character). Refusing `?` stopped the daemon from starting on
        // a configuration the bindings renderer had already accepted
        // (2026-09-23 09:37, the desk rescued and unmanaged).
        if (part.Length == 1 && !char.IsWhiteSpace(part[0]) && !char.IsControl(part[0]))
        {
            return true;
        }

        return KnownKeys.Contains(part.ToUpperInvariant());
    }

    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "SPACE", "TAB", "ENTER", "ESC", "ESCAPE", "BACKSPACE", "DELETE", "INSERT",
        "HOME", "END", "PAGEUP", "PAGEDOWN", "LEFT", "RIGHT", "UP", "DOWN",
        "PRINTSCREEN", "PAUSE",
        // The mouse wheel as a key: Hyper+WheelDown lowers a floating window,
        // Hyper+WheelUp raises it (AutoHotkey binds them the same way).
        "WHEELUP", "WHEELDOWN", "WHEELLEFT", "WHEELRIGHT",
    };

    /// <summary>Canonical text: modifiers in a fixed order, then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Hyper))
        {
            parts.Add("Hyper");
        }
        else
        {
            if (Modifiers.HasFlag(KeyModifiers.Ctrl))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(KeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(KeyModifiers.Win))
            {
                parts.Add("Win");
            }
        }

        if (Modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,

    /// <summary>
    /// This desk's Hyper: Ctrl+Alt+Win held together, the same combination
    /// <c>user/wm/sway</c> calls Mod4+Control+Mod1. Shift is added on top of
    /// it, never part of it (<c>Hyper+Shift+S</c>).
    /// </summary>
    Hyper = Ctrl | Alt | Win,
}
