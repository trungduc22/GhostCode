namespace GhostCode.Core.Hotkeys;

/// <summary>
/// Parses human-readable global hotkey gestures (e.g. "Ctrl+Shift+F9") into Win32 RegisterHotKey modifiers and virtual-key codes.
/// </summary>
public static class GlobalHotkeyParser
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public static bool TryParse(string? gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in parts)
        {
            var token = raw.ToUpperInvariant();
            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "WIN":
                    modifiers |= ModWin;
                    break;
                default:
                    if (virtualKey != 0)
                        return false;

                    if (token.StartsWith('F') && int.TryParse(token[1..], out int fNum) && fNum is >= 1 and <= 24)
                    {
                        virtualKey = (uint)(0x70 + fNum - 1);
                    }
                    else if (token.Length == 1 && token[0] is >= 'A' and <= 'Z')
                    {
                        virtualKey = (uint)token[0];
                    }
                    else if (token.Length == 1 && token[0] is >= '0' and <= '9')
                    {
                        virtualKey = (uint)token[0];
                    }
                    else if (token == "SPACE")
                    {
                        virtualKey = 0x20;
                    }
                    else
                    {
                        return false;
                    }
                    break;
            }
        }

        // Prevent hijacking ordinary printable keys without a modifier; allow function keys alone or modified chords.
        bool isFunctionKey = virtualKey is >= 0x70 and <= 0x87;
        return virtualKey != 0 && (modifiers != 0 || isFunctionKey);
    }
}
