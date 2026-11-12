using System.Windows;
using System.Windows.Media;

namespace Yesify;

/// <summary>
/// The two looks. Lightweight: flat dark panels, drawn by the CPU so the graphics driver never loads (least
/// memory). Glass: translucent panels over the artwork background, drawn by the GPU.
/// </summary>
public static class Theme
{
    public static void Apply(ResourceDictionary resources, bool lightweight)
    {
        void Set(string key, string color) =>
            resources[key] = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));

        if (lightweight)
        {
            Set("AccentBrush", "#FFFFFFFF");
            Set("AccentHoverBrush", "#E6FFFFFF");
            Set("SubtleTextBrush", "#FF9A9AA6");
            Set("FaintTextBrush", "#FF7A7A86");
            Set("InactiveIconBrush", "#FF9A9AA6");
            Set("LogoBrush", "#FFFFFFFF");
            Set("LogoForegroundBrush", "#FF0A0A0C");
            Set("ToastBrush", "#F02C2C33");
            Set("SettingsCardBrush", "#FF1A1A20");
            Set("PlayButtonBrush", "#FFF2F2F5");
            Set("PlayButtonBorderBrush", "#00000000");
            Set("PlayGlyphBrush", "#FF0A0A0C");
            Set("MenuBrush", "#FF1E1E24");
            Set("ScrollThumbBrush", "#FF3A3A44");
            Set("ScrollThumbHoverBrush", "#FF5A5A66");
            Set("ScrollThumbBorderBrush", "#00000000");
            Set("SwitchKnobOnBrush", "#FF101016");
            resources["LikedTileBrush"] = Frozen(new LinearGradientBrush(Color.FromRgb(0x4A, 0x4A, 0x55), Color.FromRgb(0x26, 0x26, 0x2C), 45));
            Set("PanelBrush", "#FF141418");
            Set("RaisedBrush", "#FF1E1E24");
            Set("HoverBrush", "#FF26262E");
            Set("SelectedBrush", "#FF2C2C33");
            Set("DividerBrush", "#FF24242B");
            Set("PanelBorderBrush", "#00000000");
            resources["MainPanelBrush"] = Frozen(new LinearGradientBrush(
                [new GradientStop(Color.FromRgb(0x26, 0x26, 0x2C), 0), new GradientStop(Color.FromRgb(0x14, 0x14, 0x18), 0.45)], 90));
        }
        else
        {
            // No purple in glass: accents are white, secondary text is translucent white.
            Set("AccentBrush", "#FFFFFFFF");
            Set("AccentHoverBrush", "#E6FFFFFF");
            Set("SubtleTextBrush", "#B3FFFFFF");
            Set("FaintTextBrush", "#80FFFFFF");
            Set("InactiveIconBrush", "#73FFFFFF");
            Set("LogoBrush", "#40FFFFFF");
            Set("LogoForegroundBrush", "#FFFFFFFF");
            Set("ToastBrush", "#99101016");
            Set("SettingsCardBrush", "#80141418"); // tint over the frost layer
            // Apple Music-style frosted play button instead of a solid white disc.
            Set("PlayButtonBrush", "#2EFFFFFF");
            Set("PlayButtonBorderBrush", "#4DFFFFFF");
            Set("PlayGlyphBrush", "#FFFFFFFF");
            // Frosted scrollbar pill.
            Set("MenuBrush", "#8C16161C"); // tint over the frost layer
            Set("ScrollThumbBrush", "#2EFFFFFF");
            Set("ScrollThumbHoverBrush", "#59FFFFFF");
            Set("ScrollThumbBorderBrush", "#33FFFFFF");
            Set("SwitchKnobOnBrush", "#FF101016");
            resources["LikedTileBrush"] = Frozen(new LinearGradientBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 45));
            Set("PanelBrush", "#59101016");
            Set("RaisedBrush", "#1FFFFFFF");
            Set("HoverBrush", "#2EFFFFFF");
            Set("SelectedBrush", "#3DFFFFFF");
            Set("DividerBrush", "#1FFFFFFF");
            Set("PanelBorderBrush", "#24FFFFFF");
            Set("MainPanelBrush", "#38101016");
        }
        Frost.IsGlass = !lightweight;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
