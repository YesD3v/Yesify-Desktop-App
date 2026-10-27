using System.Windows;
using System.Windows.Controls;

namespace Yesify;

/// <summary>
/// Lays its children out in a single row and leaves out the ones that don't fully fit, like the rows on
/// Spotify's home page: a wider window shows more tiles.
/// </summary>
public sealed class OneRowPanel : Panel
{
    public OneRowPanel() => ClipToBounds = true;

    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        var full = false;
        foreach (UIElement child in InternalChildren)
        {
            var w = child.DesiredSize.Width;
            if (!full && x + w <= final.Width + 0.5)
            {
                child.Arrange(new Rect(x, 0, w, final.Height));
                x += w;
            }
            else
            {
                full = true;
                child.Arrange(new Rect(final.Width + 10_000, 0, w, final.Height)); // clipped away
            }
        }
        return final;
    }
}
