using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Yesify;

/// <summary>
/// A thin round progress ring: a dark gray track that fills clockwise in white from the top. A negative
/// <see cref="Value"/> means there's no telling how far along: a short arc spins round instead.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new PropertyMetadata(0.0, (d, _) => ((ProgressRing)d).OnValueChanged()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Frozen(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x40)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What's drawn: eases toward <see cref="Value"/> so the ring fills smoothly between reports.</summary>
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Where the spinning arc starts, in degrees (indeterminate).</summary>
    private static readonly DependencyProperty SpinProperty = DependencyProperty.Register(
        "Spin", typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private bool _spinning;

    public ProgressRing()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => OnValueChanged();
    }

    private void OnValueChanged()
    {
        var indeterminate = Value < 0;
        if (indeterminate && IsVisible)
        {
            if (_spinning) return;
            _spinning = true;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(spin, 60);
            BeginAnimation(SpinProperty, spin);
            return;
        }
        if (_spinning)
        {
            _spinning = false;
            BeginAnimation(SpinProperty, null);
        }
        var to = Math.Clamp(Value, 0, 1);
        // Going back (a new install) jumps; going forward eases.
        if (to < (double)GetValue(ShownProperty)) { BeginAnimation(ShownProperty, null); SetValue(ShownProperty, to); return; }
        var fill = new DoubleAnimation(to, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(fill, 60);
        BeginAnimation(ShownProperty, fill);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var t = Thickness;
        var r = Math.Min(ActualWidth, ActualHeight) / 2 - t / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, t), c, r, r);

        var pen = new Pen(Fill, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (Value < 0)
        {
            var start = (double)GetValue(SpinProperty);
            DrawArc(dc, pen, c, r, start, start + 100);
            return;
        }
        var shown = (double)GetValue(ShownProperty);
        if (shown >= 0.999) dc.DrawEllipse(null, new Pen(Fill, t), c, r, r);
        else if (shown > 0.005) DrawArc(dc, pen, c, r, 0, shown * 360);
    }

    /// <summary>Clockwise from <paramref name="from"/> to <paramref name="to"/> degrees, 0 at the top.</summary>
    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double from, double to)
    {
        Point At(double deg)
        {
            var rad = (deg - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(At(from), false, false);
            g.ArcTo(At(to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static Brush Frozen(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
