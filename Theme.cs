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
            // Liner-style: a bold solid white play disc with a dark glyph.
            Set("PlayButtonBrush", "#F2FFFFFF");
            Set("PlayButtonBorderBrush", "#00FFFFFF");
            Set("PlayGlyphBrush", "#FF0A0A0E");
            // Frosted scrollbar pill.
            Set("MenuBrush", "#8C16161C"); // tint over the frost layer
            Set("ScrollThumbBrush", "#2EFFFFFF");
            Set("ScrollThumbHoverBrush", "#59FFFFFF");
            Set("ScrollThumbBorderBrush", "#33FFFFFF");
            Set("SwitchKnobOnBrush", "#FF101016");
            // Liked Songs: the frosted white glass tile with a big heart, a little bolder than before.
            resources["LikedTileBrush"] = Frozen(new LinearGradientBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF), 45));
            // Liner-style liquid glass: dark translucent slabs over a vivid cover backdrop, glass chips at
            // white 9% (hover 16%), and edges lit from the top like a pane of glass.
            Set("PanelBrush", "#4D06070C");
            Set("RaisedBrush", "#17FFFFFF");
            Set("HoverBrush", "#29FFFFFF");
            Set("SelectedBrush", "#38FFFFFF");
            Set("DividerBrush", "#1AFFFFFF");
            Set("MainPanelBrush", "#2606070C");
            Set("GlassChipBrush", "#17FFFFFF");
            Set("PlayerBarBrush", "#5906070C");
            resources["PanelBorderBrush"] = Frozen(new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0), new GradientStop(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF), 0.35),
                 new GradientStop(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 1)],
                new System.Windows.Point(0, 0), new System.Windows.Point(0, 1)));
        }
        Frost.IsGlass = true;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
