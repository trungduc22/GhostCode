using GhostCode.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace GhostCode.App.Theming;

/// <summary>
/// Centralized GhostCode color design system:
/// - Ghost Blue signature default (#2563EB Light / #60A5FA Dark)
/// - 7 Accent Presets (GhostBlue, Violet, Teal, Emerald, Amber, WindowsAccent, CustomAccent)
/// - Semantic Status Palette (Green = Protected/Synced, Blue = Active/Detecting, Amber = Queued/Pending, Red = Error, Neutral = Idle)
/// Preserves native WinUI 3 Mica backdrop & caption button legibility across Light & Dark themes.
/// </summary>
public static class GhostThemePalette
{
    public static readonly Color GhostBlueLight = Color.FromArgb(255, 0x25, 0x63, 0xEB);
    public static readonly Color GhostBlueDark = Color.FromArgb(255, 0x60, 0xA5, 0xFA);

    public static (Color Light, Color Dark) ResolveAccentPair(AccentColorPreset preset, string? customHex) => preset switch
    {
        AccentColorPreset.Violet => (Color.FromArgb(255, 0x7C, 0x3A, 0xED), Color.FromArgb(255, 0xA7, 0x8B, 0xFA)),
        AccentColorPreset.Teal => (Color.FromArgb(255, 0x0D, 0x94, 0x88), Color.FromArgb(255, 0x2D, 0xD4, 0xBF)),
        AccentColorPreset.Emerald => (Color.FromArgb(255, 0x05, 0x96, 0x69), Color.FromArgb(255, 0x34, 0xD3, 0x99)),
        AccentColorPreset.Amber => (Color.FromArgb(255, 0xD9, 0x77, 0x06), Color.FromArgb(255, 0xFB, 0xBF, 0x24)),
        AccentColorPreset.WindowsAccent => ResolveWindowsAccentPair(),
        AccentColorPreset.CustomAccent => ResolveCustomAccentPair(customHex),
        _ => (GhostBlueLight, GhostBlueDark)
    };

    public static bool TryParseHexColor(string? hex, out Color color)
    {
        color = GhostBlueLight;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        var clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 && uint.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            color = Color.FromArgb(
                255,
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
            return true;
        }
        return false;
    }

    public static Color Apply(ElementTheme resolvedTheme, AccentColorPreset preset, string? customHex)
    {
        bool isDark = resolvedTheme == ElementTheme.Dark;
        var (lightAccent, darkAccent) = ResolveAccentPair(preset, customHex);
        var activeAccent = isDark ? darkAccent : lightAccent;
        var softAccent = Color.FromArgb(isDark ? (byte)42 : (byte)28, activeAccent.R, activeAccent.G, activeAccent.B);
        var hoverAccent = AdjustBrightness(activeAccent, isDark ? 0.90 : 1.08);
        var pressedAccent = AdjustBrightness(activeAccent, isDark ? 0.80 : 0.88);
        var onAccentForeground = RelativeLuminance(activeAccent) > 0.42
            ? Color.FromArgb(255, 0x0F, 0x17, 0x2A)
            : Colors.White;

        var resources = Application.Current?.Resources;
        if (resources is null)
            return activeAccent;

        resources["SystemAccentColor"] = activeAccent;
        resources["SystemAccentColorLight1"] = AdjustBrightness(activeAccent, 1.15);
        resources["SystemAccentColorLight2"] = AdjustBrightness(activeAccent, 1.30);
        resources["SystemAccentColorLight3"] = AdjustBrightness(activeAccent, 1.45);
        resources["SystemAccentColorDark1"] = AdjustBrightness(activeAccent, 0.85);
        resources["SystemAccentColorDark2"] = AdjustBrightness(activeAccent, 0.72);
        resources["SystemAccentColorDark3"] = AdjustBrightness(activeAccent, 0.58);

        SetBrush(resources, "AccentFillColorDefaultBrush", activeAccent);
        SetBrush(resources, "AccentFillColorSecondaryBrush", hoverAccent);
        SetBrush(resources, "AccentFillColorTertiaryBrush", pressedAccent);
        SetBrush(resources, "AccentTextFillColorPrimaryBrush", activeAccent);
        SetBrush(resources, "TextOnAccentFillColorPrimaryBrush", onAccentForeground);

        SetBrush(resources, "GhostAccentBrush", activeAccent);
        SetBrush(resources, "GhostAccentSoftBrush", softAccent);

        // Centralized semantic status palette
        SetBrush(resources, "GhostSuccessBgBrush", isDark ? Color.FromArgb(44, 0x10, 0xB9, 0x81) : Color.FromArgb(30, 0x05, 0x96, 0x69));
        SetBrush(resources, "GhostSuccessFgBrush", isDark ? Color.FromArgb(255, 0x34, 0xD3, 0x99) : Color.FromArgb(255, 0x04, 0x78, 0x57));

        SetBrush(resources, "GhostActiveBgBrush", isDark ? Color.FromArgb(44, 0x3B, 0x82, 0xF6) : Color.FromArgb(30, 0x25, 0x63, 0xEB));
        SetBrush(resources, "GhostActiveFgBrush", isDark ? Color.FromArgb(255, 0x60, 0xA5, 0xFA) : Color.FromArgb(255, 0x1D, 0x4E, 0xD8));

        SetBrush(resources, "GhostWarningBgBrush", isDark ? Color.FromArgb(44, 0xF5, 0x9E, 0x0B) : Color.FromArgb(30, 0xD9, 0x77, 0x06));
        SetBrush(resources, "GhostWarningFgBrush", isDark ? Color.FromArgb(255, 0xFB, 0xBF, 0x24) : Color.FromArgb(255, 0xB4, 0x53, 0x09));

        SetBrush(resources, "GhostErrorBgBrush", isDark ? Color.FromArgb(44, 0xEF, 0x44, 0x44) : Color.FromArgb(30, 0xDC, 0x26, 0x26));
        SetBrush(resources, "GhostErrorFgBrush", isDark ? Color.FromArgb(255, 0xF8, 0x71, 0x71) : Color.FromArgb(255, 0xB9, 0x1C, 0x1C));

        SetBrush(resources, "GhostNeutralBgBrush", isDark ? Color.FromArgb(36, 0x94, 0xA3, 0xB8) : Color.FromArgb(26, 0x64, 0x74, 0x8B));
        SetBrush(resources, "GhostNeutralFgBrush", isDark ? Color.FromArgb(255, 0xCB, 0xD5, 0xE1) : Color.FromArgb(255, 0x47, 0x55, 0x69));

        return activeAccent;
    }

    private static (Color Light, Color Dark) ResolveWindowsAccentPair()
    {
        try
        {
            var ui = new UISettings();
            var sys = ui.GetColorValue(UIColorType.Accent);
            var darkFriendly = RelativeLuminance(sys) < 0.25 ? MixWithWhite(sys, 0.35) : sys;
            var lightFriendly = RelativeLuminance(sys) > 0.55 ? AdjustBrightness(sys, 0.72) : sys;
            return (lightFriendly, darkFriendly);
        }
        catch
        {
            return (GhostBlueLight, GhostBlueDark);
        }
    }

    private static (Color Light, Color Dark) ResolveCustomAccentPair(string? customHex)
    {
        if (!TryParseHexColor(customHex, out var c))
            return (GhostBlueLight, GhostBlueDark);

        var darkFriendly = RelativeLuminance(c) < 0.22 ? MixWithWhite(c, 0.35) : c;
        var lightFriendly = RelativeLuminance(c) > 0.58 ? AdjustBrightness(c, 0.72) : c;
        return (lightFriendly, darkFriendly);
    }

    private static void SetBrush(ResourceDictionary dict, string key, Color color)
    {
        if (dict.TryGetValue(key, out var existing) && existing is SolidColorBrush scb)
        {
            scb.Color = color;
        }
        else
        {
            dict[key] = new SolidColorBrush(color);
        }
    }

    private static Color AdjustBrightness(Color c, double factor) => Color.FromArgb(
        c.A,
        (byte)Math.Clamp((int)Math.Round(c.R * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.G * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.B * factor), 0, 255));

    private static Color MixWithWhite(Color c, double weightWhite) => Color.FromArgb(
        c.A,
        (byte)Math.Clamp((int)Math.Round(c.R + (255 - c.R) * weightWhite), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.G + (255 - c.G) * weightWhite), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.B + (255 - c.B) * weightWhite), 0, 255));

    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
}
