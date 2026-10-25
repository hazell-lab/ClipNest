using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace ClipNest.Core.Settings;

/// <summary>
/// modifier flags. the numbers match what windows expects for global hotkeys.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// a keyboard shortcut such as ctrl+shift+v, stored as text in settings.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public const string DefaultText = "Ctrl+Shift+V";

    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, "V");

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
    };

    /// <summary>
    /// the windows virtual key number for the key part.
    /// </summary>
    public int VirtualKey => TryGetVirtualKey(Key, out var vk) ? vk : 0;

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var flag = ParseModifier(parts[i]);
            if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag))
            {
                return false;
            }

            modifiers |= flag;
        }

        // shift alone would steal normal typing, so a stronger modifier is needed
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            return false;
        }

        var key = NormalizeKey(parts[^1]);
        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    public static HotkeyGesture ParseOrDefault(string? text) =>
        TryParse(text, out var gesture) ? gesture : Default;

    public override string ToString()
    {
        var builder = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            builder.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            builder.Append("Alt+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            builder.Append("Shift+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            builder.Append("Win+");
        }

        builder.Append(Key);
        return builder.ToString();
    }

    /// <summary>
    /// returns the standard spelling of a key name, or null if it is not allowed.
    /// </summary>
    public static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        key = key.Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return key.ToUpperInvariant();
        }

        if ((key[0] == 'F' || key[0] == 'f')
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var fn)
            && fn is >= 1 and <= 24)
        {
            return "F" + fn.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var name in NamedKeys.Keys)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    public static bool TryGetVirtualKey(string? key, out int virtualKey)
    {
        virtualKey = 0;
        var normalized = NormalizeKey(key);
        if (normalized is null)
        {
            return false;
        }

        if (normalized.Length == 1)
        {
            // letters and digits use their own character codes
            virtualKey = normalized[0];
            return true;
        }

        if (normalized[0] == 'F' && normalized.Length > 1 && char.IsAsciiDigit(normalized[1]))
        {
            virtualKey = 0x70 + int.Parse(normalized.AsSpan(1), CultureInfo.InvariantCulture) - 1;
            return true;
        }

        return NamedKeys.TryGetValue(normalized, out virtualKey);
    }

    private static HotkeyModifiers ParseModifier(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "ctl" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" or "meta" or "super" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };
}
