using Avalonia.Input;

namespace MapleLite.Sdk;

/// <summary>
/// System-wide keyboard shortcuts, so things like "take a snapshot" work while the game is the active window.
/// MapleLite registers them with the OS (RegisterHotKey on Windows): no keyboard hooks, nothing loaded into the game,
/// and no input is ever sent to it. A registered combination is taken by MapleLite, so the game doesn't see it
/// while MapleLite runs: pick combinations the game doesn't use.
/// </summary>
public interface IHotkeys
{
    /// <summary>Hotkeys work on this computer (Windows only so far).</summary>
    bool Supported { get; }
    /// <summary>
    /// Registers a shortcut. <paramref name="gesture"/> is text such as "Ctrl+Alt+S" (see <see cref="HotkeyText"/>);
    /// null or empty registers nothing until <see cref="IHotkey.Change"/>. <paramref name="pressed"/> runs on a
    /// background thread: use context.OnUi for UI work. Removed when the plugin stops or the result is disposed.
    /// </summary>
    IHotkey Register(string name, string? gesture, Action pressed);
}

public interface IHotkey : IDisposable
{
    string Name { get; }
    /// <summary>The combination asked for (normalized), or null for none.</summary>
    string? Gesture { get; }
    /// <summary>The OS accepted it: pressing it anywhere calls the handler.</summary>
    bool Active { get; }
    /// <summary>Why it isn't active (taken by another app, not supported here, …), or null.</summary>
    string? Error { get; }
    /// <summary>Switches to another combination (null or empty for none). False when it couldn't be registered (see <see cref="Error"/>).</summary>
    bool Change(string? gesture);
}

/// <summary>Reads and writes shortcut text like "Ctrl+Alt+S" or "Shift+F9", using Avalonia's key names.</summary>
public static class HotkeyText
{
    /// <summary>The text for a key press (as a KeyDown handler sees it), or null for a lone modifier key.</summary>
    public static string? Format(KeyModifiers mods, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return null;
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out KeyModifiers mods, out Key key)
    {
        mods = KeyModifiers.None; key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Control; continue;
                case "alt": mods |= KeyModifiers.Alt; continue;
                case "shift": mods |= KeyModifiers.Shift; continue;
                case "win" or "meta" or "cmd": mods |= KeyModifiers.Meta; continue;
            }
            if (key != Key.None) return false; // two keys
            var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse(name, true, out key) || key == Key.None) return false;
        }
        return key != Key.None;
    }

    /// <summary>Normalized text ("ctrl+alt+s" → "Ctrl+Alt+S"), or null when it isn't a shortcut.</summary>
    public static string? Normalize(string? text) => TryParse(text, out var m, out var k) ? Format(m, k) : null;

    /// <summary>
    /// Why a shortcut is a poor choice, or null. A letter, digit or space with no Ctrl/Alt/Win would be taken from the
    /// game (and from typing everywhere else).
    /// </summary>
    public static string? Problem(string? text)
    {
        if (!TryParse(text, out var m, out var k)) return "That isn't a key combination.";
        bool typing = k is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Space or Key.Enter or Key.Tab or Key.Back or Key.Escape;
        if (typing && (m & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
            return "Add Ctrl or Alt: on its own that key would stop working in the game and everywhere else.";
        return null;
    }

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        Key.Snapshot => "PrintScreen",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        _ => k.ToString(),
    };
}
