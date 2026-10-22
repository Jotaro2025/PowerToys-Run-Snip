// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Input;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Parses and formats plugin global hotkeys such as <c>Ctrl+Shift+S</c>.
/// Empty / whitespace means disabled.
/// </summary>
internal static class HotkeyParser
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// Tries to parse a display string into Win32 modifiers + virtual-key code.
    /// </summary>
    /// <returns>True when empty (disabled) or a valid modifier+key combination.</returns>
    public static bool TryParse(string text, out uint modifiers, out uint virtualKey, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "Hotkey needs at least one modifier and one key (e.g. Ctrl+Shift+S).";
            return false;
        }

        Key? key = null;
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (IsModifier(part, out var mod))
            {
                modifiers |= mod;
                continue;
            }

            if (key.HasValue)
            {
                error = "Hotkey may contain only one non-modifier key.";
                return false;
            }

            if (!TryParseKey(part, out var parsed))
            {
                error = $"Unrecognized key: {part}";
                return false;
            }

            key = parsed;
        }

        if (modifiers == 0)
        {
            error = "Hotkey needs at least one modifier (Ctrl / Alt / Shift / Win).";
            return false;
        }

        if (!key.HasValue)
        {
            error = "Hotkey needs a non-modifier key.";
            return false;
        }

        if (IsModifierKey(key.Value))
        {
            error = "Hotkey key cannot be a modifier alone.";
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        if (virtualKey == 0)
        {
            error = $"Key has no virtual-key mapping: {key.Value}";
            return false;
        }

        return true;
    }

    /// <summary>Formats modifiers + WPF key into a stable display string (Ctrl+Shift+S).</summary>
    public static string Format(uint modifiers, Key key)
    {
        var sb = new StringBuilder(32);
        if ((modifiers & ModControl) != 0)
        {
            Append(sb, "Ctrl");
        }

        if ((modifiers & ModAlt) != 0)
        {
            Append(sb, "Alt");
        }

        if ((modifiers & ModShift) != 0)
        {
            Append(sb, "Shift");
        }

        if ((modifiers & ModWin) != 0)
        {
            Append(sb, "Win");
        }

        Append(sb, KeyToToken(key));
        return sb.ToString();
    }

    /// <summary>Round-trips a string through parse → format when valid; otherwise returns trimmed input.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (!TryParse(text, out var mods, out var vk, out _))
        {
            return text.Trim();
        }

        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        return Format(mods, key);
    }

    /// <summary>Builds modifiers from the current keyboard state (for KeyDown capture).</summary>
    public static uint ModifiersFromKeyboard()
    {
        uint mods = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            mods |= ModControl;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            mods |= ModAlt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            mods |= ModShift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            mods |= ModWin;
        }

        return mods;
    }

    public static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None;

    private static void Append(StringBuilder sb, string token)
    {
        if (sb.Length > 0)
        {
            sb.Append('+');
        }

        sb.Append(token);
    }

    private static bool IsModifier(string part, out uint mod)
    {
        mod = 0;
        switch (part.ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                mod = ModControl;
                return true;
            case "ALT":
            case "MENU":
                mod = ModAlt;
                return true;
            case "SHIFT":
                mod = ModShift;
                return true;
            case "WIN":
            case "WINDOWS":
            case "META":
            case "CMD":
                mod = ModWin;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseKey(string part, out Key key)
    {
        key = Key.None;

        // Single letter / digit
        if (part.Length == 1)
        {
            var c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = (Key)((int)Key.A + (c - 'A'));
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                key = (Key)((int)Key.D0 + (c - '0'));
                return true;
            }
        }

        // F1–F24
        if (part.Length >= 2
            && (part[0] == 'F' || part[0] == 'f')
            && int.TryParse(part.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fn)
            && fn is >= 1 and <= 24)
        {
            key = (Key)((int)Key.F1 + (fn - 1));
            return true;
        }

        // Named keys
        var map = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = Key.Space,
            ["Spacebar"] = Key.Space,
            ["Tab"] = Key.Tab,
            ["Enter"] = Key.Enter,
            ["Return"] = Key.Enter,
            ["Escape"] = Key.Escape,
            ["Esc"] = Key.Escape,
            ["Back"] = Key.Back,
            ["Backspace"] = Key.Back,
            ["Delete"] = Key.Delete,
            ["Del"] = Key.Delete,
            ["Insert"] = Key.Insert,
            ["Ins"] = Key.Insert,
            ["Home"] = Key.Home,
            ["End"] = Key.End,
            ["PageUp"] = Key.PageUp,
            ["PgUp"] = Key.PageUp,
            ["PageDown"] = Key.PageDown,
            ["PgDn"] = Key.PageDown,
            ["Up"] = Key.Up,
            ["Down"] = Key.Down,
            ["Left"] = Key.Left,
            ["Right"] = Key.Right,
            ["PrintScreen"] = Key.PrintScreen,
            ["PrtSc"] = Key.PrintScreen,
            ["OemPlus"] = Key.OemPlus,
            ["OemMinus"] = Key.OemMinus,
            ["Plus"] = Key.OemPlus,
            ["Minus"] = Key.OemMinus,
        };

        if (map.TryGetValue(part, out key))
        {
            return true;
        }

        return Enum.TryParse(part, ignoreCase: true, out key) && key != Key.None && !IsModifierKey(key);
    }

    private static string KeyToToken(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return ((char)('A' + (key - Key.A))).ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return "F" + (1 + (key - Key.F1)).ToString(CultureInfo.InvariantCulture);
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Escape => "Esc",
            Key.Delete => "Delete",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.OemPlus => "Plus",
            Key.OemMinus => "Minus",
            _ => key.ToString(),
        };
    }
}
