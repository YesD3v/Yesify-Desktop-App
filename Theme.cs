using System.Windows;
using System.Windows.Media;

namespace Yesify;

/// <summary>
/// The glass look: translucent panels over the blurred artwork background, drawn by the GPU.
/// </summary>
public static class Theme
{
    public static void Apply(ResourceDictionary resources)
    {
        void Set(string key, string color) =>
            resources[key] = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));

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
        Frost.IsGlass = true;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
