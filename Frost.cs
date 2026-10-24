using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Yesify;

/// <summary>A window whose content can be shown, blurred, behind a frosted pop-up.</summary>
public interface IFrostSource
{
    /// <summary>The layers that make up what's behind a pop-up, bottom to top (not the pop-ups themselves).</summary>
    IEnumerable<Visual> FrostLayers { get; }
}

/// <summary>
/// Frosted glass for pop-ups (dialogs, menus). WPF can't blur what's behind an element, so a frost layer
/// paints a live picture of the window under it (<see cref="IFrostSource.FrostLayers"/>), lined up to its
/// own position on screen, and blurs that. Put <c>local:Frost.Enabled="True"</c> on an empty Border at the
/// back of the pop-up; it only does anything in glass mode (<see cref="IsGlass"/>).
/// </summary>
public static class Frost
{
    /// <summary>Set by the theme: frost in glass mode, plain backgrounds in lightweight.</summary>
    public static bool IsGlass { get; set; }

    private const double BlurRadius = 36;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Frost), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border || e.NewValue is not true) return;
        border.IsVisibleChanged += (_, _) => Refresh(border);
        border.SizeChanged += (_, _) => Refresh(border);
        border.Loaded += (_, _) => Refresh(border);
    }

    private static void Refresh(Border border)
    {
        if (!IsGlass || !border.IsVisible || border.ActualWidth < 1)
        {
            border.Child = null;
            return;
        }

        // Defer until the pop-up has been placed, so its screen position is final.
        border.Dispatcher.BeginInvoke(() =>
        {
            if (!border.IsVisible || PresentationSource.FromVisual(border) == null) return;
            var topLeft = border.PointToScreen(new Point(0, 0));
            var window = FindWindowUnder(topLeft);
            if (window is not IFrostSource source || window.Content is not UIElement content || PresentationSource.FromVisual(content) == null) return;

            // Where the pop-up sits, in the window's own coordinates (DIPs).
            var at = content.PointFromScreen(topLeft);
            var dpi = VisualTreeHelper.GetDpi(border);
            var scale = VisualTreeHelper.GetDpi(content).DpiScaleX / dpi.DpiScaleX;
            var area = new Rect(at.X - BlurRadius, at.Y - BlurRadius,
                                border.ActualWidth * scale + 2 * BlurRadius, border.ActualHeight * scale + 2 * BlurRadius);

            var stack = new Grid { Margin = new Thickness(-BlurRadius), IsHitTestVisible = false };
            if (window.Background != null) stack.Children.Add(new Rectangle { Fill = window.Background });
            foreach (var layer in source.FrostLayers)
                stack.Children.Add(new Rectangle
                {
                    Fill = new VisualBrush(layer)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = area,
                        Stretch = Stretch.Fill,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                    },
                });
            stack.Effect = new BlurEffect { Radius = BlurRadius, RenderingBias = RenderingBias.Performance };

            // Clip the blur to the pop-up's rounded shape.
            var r = border.CornerRadius.TopLeft;
            border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), r, r);
            border.Child = stack;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static Window? FindWindowUnder(Point screen)
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not IFrostSource || !w.IsVisible || PresentationSource.FromVisual(w) == null) continue;
            var origin = w.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(w);
            var bounds = new Rect(origin, new Size(w.ActualWidth * dpi.DpiScaleX, w.ActualHeight * dpi.DpiScaleY));
            if (bounds.Contains(screen)) return w;
        }
        return null;
    }
}
