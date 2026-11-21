using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Yesify;

/// <summary>
/// A bold, rounded vector icon (Liner-style) for a Segoe Fluent Icons code point. The common glyphs are
/// redrawn as thick shapes with round caps (play/pause are solid); anything not redrawn falls back to the
/// icon font. Takes its color and size from the inherited Foreground / FontSize, like text would.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(Icon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    /// <summary>Stroke (outline) and fill parts of an icon, drawn in a 24×24 box.</summary>
    private sealed record Shape(Geometry? Stroke, Geometry? Fill, bool Mirror = false, double Weight = 2.1);

    private static readonly Dictionary<char, Shape> Shapes = new();
    private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    static Icon()
    {
        void Add(char c, string? stroke, string? fill = null, bool mirror = false, double weight = 2.1)
        {
            Geometry? G(string? d) { if (d == null) return null; var g = Geometry.Parse(d); g.Freeze(); return g; }
            Shapes[c] = new Shape(G(stroke), G(fill), mirror, weight);
        }

        const string play = "M7.5,5.3 C7.5,4.1 8.8,3.4 9.8,4 L19.3,10.3 C20.3,10.9 20.3,12.4 19.3,13 L9.8,19.6 C8.8,20.3 7.5,19.6 7.5,18.4 Z";
        Add('', null, play);
        Add('', null, play);
        Add('', null, "M6,4 H9 A1.75,1.75 0 0 1 10.75,5.75 V18.25 A1.75,1.75 0 0 1 9,20 H6 A1.75,1.75 0 0 1 4.25,18.25 V5.75 A1.75,1.75 0 0 1 6,4 Z " +
                              "M15,4 H18 A1.75,1.75 0 0 1 19.75,5.75 V18.25 A1.75,1.75 0 0 1 18,20 H15 A1.75,1.75 0 0 1 13.25,18.25 V5.75 A1.75,1.75 0 0 1 15,4 Z");
        const string next = "M4.5,6.6 C4.5,5.4 5.8,4.8 6.8,5.4 L15.2,10.8 C16.1,11.4 16.1,12.6 15.2,13.2 L6.8,18.6 C5.8,19.2 4.5,18.6 4.5,17.4 Z " +
                            "M17.8,5.5 A1.35,1.35 0 0 1 20.5,5.5 V18.5 A1.35,1.35 0 0 1 17.8,18.5 Z";
        Add('', null, next);
        Add('', null, next, mirror: true);
        Add('', "M3.5,7 H6.5 C9,7 10.2,8.6 12,12 C13.8,15.4 15,17 17.5,17 H20 M3.5,17 H6.5 C8.2,17 9.3,16.2 10.4,14.6 M13.6,9.4 C14.7,7.8 15.8,7 17.5,7 H20 " +
                       "M17.5,4.5 L20,7 L17.5,9.5 M17.5,14.5 L20,17 L17.5,19.5");
        const string repeat = "M4,12 V10 A4,4 0 0 1 8,6 H19 M16.5,3.5 L19,6 L16.5,8.5 M20,12 V14 A4,4 0 0 1 16,18 H5 M7.5,20.5 L5,18 L7.5,15.5";
        Add('', repeat);
        Add('', repeat + " M11,10.6 L12.4,9.7 V14.3");
        const string heart = "M12,20 C12,20 3.4,15.2 3.4,9.3 A4.6,4.6 0 0 1 12,7.4 A4.6,4.6 0 0 1 20.6,9.3 C20.6,15.2 12,20 12,20 Z";
        Add('', heart);
        Add('', null, heart);
        Add('', "M6,4.5 H18 A2.5,2.5 0 0 1 20.5,7 V14.5 A2.5,2.5 0 0 1 18,17 H12.5 L8,20.5 V17 H6 A2.5,2.5 0 0 1 3.5,14.5 V7 A2.5,2.5 0 0 1 6,4.5 Z M8,9 H16 M8,12.5 H13");
        Add('', "M4,6 H17 M4,11.5 H17 M4,17 H11",
            "M14.5,14.4 C14.5,13.6 15.3,13.2 16,13.6 L20.4,16.3 C21,16.7 21,17.5 20.4,17.9 L16,20.6 C15.3,21 14.5,20.6 14.5,19.8 Z");
        Add('', "M16.5,9 A4,4 0 0 1 16.5,15 M19.2,6.3 A7.6,7.6 0 0 1 19.2,17.7",
            "M3.5,10 A1.5,1.5 0 0 1 5,8.5 H7.6 L11.4,5.2 C12.1,4.6 13.2,5.1 13.2,6 V18 C13.2,18.9 12.1,19.4 11.4,18.8 L7.6,15.5 H5 A1.5,1.5 0 0 1 3.5,14 Z");
        Add('', null, "M3.2,12 A2.1,2.1 0 1 0 7.4,12 A2.1,2.1 0 1 0 3.2,12 Z M9.9,12 A2.1,2.1 0 1 0 14.1,12 A2.1,2.1 0 1 0 9.9,12 Z M16.6,12 A2.1,2.1 0 1 0 20.8,12 A2.1,2.1 0 1 0 16.6,12 Z");
        Add('', "M12,5 V19 M5,12 H19");
        Add('', "M5,12.5 L10,17.5 L19.5,7");
        Add('', "M4,10.5 A6.5,6.5 0 1 0 17,10.5 A6.5,6.5 0 1 0 4,10.5 Z M15.6,15.6 L20,20");
        Add('', "M4,10.4 L12,4 L20,10.4 V18.5 A2,2 0 0 1 18,20.5 H15 V15.5 A1.5,1.5 0 0 0 13.5,14 H10.5 A1.5,1.5 0 0 0 9,15.5 V20.5 H6 A2,2 0 0 1 4,18.5 Z");
        Add('', "M5,4.5 V19.5 M9.5,4.5 V19.5 M14,5.5 L18.5,19");
        Add('', "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M3.8,12 H20.2 M12,3.5 C8.8,6.5 8.8,17.5 12,20.5 M12,3.5 C15.2,6.5 15.2,17.5 12,20.5", weight: 1.9);
        Add('', "M19.5,12 H5 M11,6 L5,12 L11,18");
        Add('', "M4,9 V6 A2,2 0 0 1 6,4 H9 M15,4 H18 A2,2 0 0 1 20,6 V9 M20,15 V18 A2,2 0 0 1 18,20 H15 M9,20 H6 A2,2 0 0 1 4,18 V15");
        // Cinema: a wide screen with a play mark.
        Add('', "M5,5 H19 A2.5,2.5 0 0 1 21.5,7.5 V15.5 A2.5,2.5 0 0 1 19,18 H5 A2.5,2.5 0 0 1 2.5,15.5 V7.5 A2.5,2.5 0 0 1 5,5 Z M8.5,21 H15.5",
            "M10,9.4 C10,8.8 10.6,8.4 11.2,8.8 L14.6,10.9 C15.1,11.2 15.1,11.8 14.6,12.1 L11.2,14.2 C10.6,14.6 10,14.2 10,13.6 Z");
        Add('', "M6,9 L12,15 L18,9"); // chevron down
        Add('\uE993', "M16.5,9 A4,4 0 0 1 16.5,15",
            "M3.5,10 A1.5,1.5 0 0 1 5,8.5 H7.6 L11.4,5.2 C12.1,4.6 13.2,5.1 13.2,6 V18 C13.2,18.9 12.1,19.4 11.4,18.8 L7.6,15.5 H5 A1.5,1.5 0 0 1 3.5,14 Z"); // volume, low
        Add('\uE73F', "M9,4 V7 A2,2 0 0 1 7,9 H4 M15,4 V7 A2,2 0 0 0 17,9 H20 M20,15 H17 A2,2 0 0 0 15,17 V20 M4,15 H7 A2,2 0 0 1 9,17 V20"); // exit full screen
        Add('\uE74F', "M16.2,9.4 L21,14.2 M21,9.4 L16.2,14.2",
            "M3.5,10 A1.5,1.5 0 0 1 5,8.5 H7.6 L11.4,5.2 C12.1,4.6 13.2,5.1 13.2,6 V18 C13.2,18.9 12.1,19.4 11.4,18.8 L7.6,15.5 H5 A1.5,1.5 0 0 1 3.5,14 Z"); // muted
        Add('\uEC4F', "M9.5,17.5 V6 L19.5,4.2 V15.5",
            "M4.2,17.6 A2.7,2.3 0 1 0 9.6,17.6 A2.7,2.3 0 1 0 4.2,17.6 Z M14.2,15.6 A2.7,2.3 0 1 0 19.6,15.6 A2.7,2.3 0 1 0 14.2,15.6 Z"); // music note
        Add('\uE916', "M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M12,7.5 V12 L15,14"); // clock
        Add('', "M15,3.8 L20.2,9 L16.6,10.6 L13.2,14 L13.6,17.8 L12.3,19.1 L4.9,11.7 L6.2,10.4 L10,10.8 L13.4,7.4 Z M8.6,15.4 L4,20"); // pin
        // Grid / list view toggles.
        Add('', null, "M6,4 H9 A2,2 0 0 1 11,6 V9 A2,2 0 0 1 9,11 H6 A2,2 0 0 1 4,9 V6 A2,2 0 0 1 6,4 Z M15,4 H18 A2,2 0 0 1 20,6 V9 A2,2 0 0 1 18,11 H15 A2,2 0 0 1 13,9 V6 A2,2 0 0 1 15,4 Z " +
                              "M6,13 H9 A2,2 0 0 1 11,15 V18 A2,2 0 0 1 9,20 H6 A2,2 0 0 1 4,18 V15 A2,2 0 0 1 6,13 Z M15,13 H18 A2,2 0 0 1 20,15 V18 A2,2 0 0 1 18,20 H15 A2,2 0 0 1 13,18 V15 A2,2 0 0 1 15,13 Z");
        Add('', "M9,6 H20 M9,12 H20 M9,18 H20",
            "M3.5,6 A1.4,1.4 0 1 0 6.3,6 A1.4,1.4 0 1 0 3.5,6 Z M3.5,12 A1.4,1.4 0 1 0 6.3,12 A1.4,1.4 0 1 0 3.5,12 Z M3.5,18 A1.4,1.4 0 1 0 6.3,18 A1.4,1.4 0 1 0 3.5,18 Z");
        Add('', "M10,14 L14,10 M8.5,11.5 L6.8,13.2 A3.5,3.5 0 0 0 11.8,18.2 L13.5,16.5 M15.5,12.5 L17.2,10.8 A3.5,3.5 0 0 0 12.2,5.8 L10.5,7.5"); // link
        // Window caption: same weight as everything else.
        Add('', "M6,12 H18");
        Add('', "M7.5,6 H16.5 A1.5,1.5 0 0 1 18,7.5 V16.5 A1.5,1.5 0 0 1 16.5,18 H7.5 A1.5,1.5 0 0 1 6,16.5 V7.5 A1.5,1.5 0 0 1 7.5,6 Z");
        Add('', "M7,9 H14 A1.5,1.5 0 0 1 15.5,10.5 V17.5 A1.5,1.5 0 0 1 14,19 H7 A1.5,1.5 0 0 1 5.5,17.5 V10.5 A1.5,1.5 0 0 1 7,9 Z M9,6 H17 A1.5,1.5 0 0 1 18.5,7.5 V15");
        Add('', "M6.5,6.5 L17.5,17.5 M17.5,6.5 L6.5,17.5");
    }

    /// <summary>The icon as a picture (e.g. a taskbar thumbnail button), drawn in <paramref name="brush"/>.</summary>
    public static ImageSource ToImage(char glyph, Brush brush)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        if (Shapes.TryGetValue(glyph, out var shape))
        {
            if (shape.Mirror) group.Transform = new ScaleTransform(-1, 1, 12, 12);
            if (shape.Fill != null) group.Children.Add(new GeometryDrawing(brush, null, shape.Fill));
            if (shape.Stroke != null)
                group.Children.Add(new GeometryDrawing(null, new Pen(brush, shape.Weight + 0.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, shape.Stroke));
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private double Box => Math.Round(FontSize * 1.25);

    protected override Size MeasureOverride(Size availableSize) => new(Box, Box);

    protected override void OnRender(DrawingContext dc)
    {
        var text = Glyph;
        if (string.IsNullOrEmpty(text)) return;
        var box = Box;
        var origin = new Point((ActualWidth - box) / 2, (ActualHeight - box) / 2);

        if (text.Length == 1 && Shapes.TryGetValue(text[0], out var shape))
        {
            var k = box / 24;
            var t = new TransformGroup();
            if (shape.Mirror) t.Children.Add(new ScaleTransform(-1, 1, 12, 12));
            t.Children.Add(new ScaleTransform(k, k));
            t.Children.Add(new TranslateTransform(origin.X, origin.Y));
            dc.PushTransform(t);
            if (shape.Fill != null) dc.DrawGeometry(Foreground, null, shape.Fill);
            if (shape.Stroke != null)
            {
                // At least ~1.4 px on screen, so small icons (window buttons) don't go thin.
                var w = Math.Max(shape.Weight, 1.45 / k);
                dc.DrawGeometry(null, new Pen(Foreground, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, shape.Stroke);
            }
            dc.Pop();
            return;
        }

        // Not redrawn: the icon font, centered on its own ink.
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var ink = ft.BuildGeometry(new Point(0, 0)).Bounds;
        if (ink.IsEmpty) return;
        dc.DrawText(ft, new Point(ActualWidth / 2 - ink.X - ink.Width / 2, ActualHeight / 2 - ink.Y - ink.Height / 2));
    }
}

/// <summary>Shows string content (an icon font code point) as an <see cref="Icon"/>; anything else (a drawing,
/// a panel) is shown as it is.</summary>
public sealed class GlyphTemplateSelector : System.Windows.Controls.DataTemplateSelector
{
    public DataTemplate? Glyph { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is string ? Glyph : null;
}
