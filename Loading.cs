using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Yesify;

/// <summary>
/// Three dots that rise and brighten in a wave: "working on it", without words. The animation only runs
/// while the dots are on screen, so a hidden indicator costs nothing.
/// </summary>
public sealed class LoadingDots : StackPanel
{
    public static readonly DependencyProperty DotSizeProperty = DependencyProperty.Register(
        nameof(DotSize), typeof(double), typeof(LoadingDots), new PropertyMetadata(6.0, (d, _) => ((LoadingDots)d).Build()));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LoadingDots), new PropertyMetadata(Brushes.White, (d, _) => ((LoadingDots)d).Build()));

    public double DotSize
    {
        get => (double)GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private const double Period = 1.1; // s for one wave across the dots

    public LoadingDots()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Build();
        IsVisibleChanged += (_, _) => Animate();
    }

    private void Build()
    {
        Children.Clear();
        for (var i = 0; i < 3; i++)
        {
            Children.Add(new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                Fill = Fill,
                Opacity = 0.3,
                Margin = new Thickness(i == 0 ? 0 : DotSize * 0.7, DotSize * 0.6, 0, DotSize * 0.6),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(0.8, 0.8), new TranslateTransform() } },
            });
        }
        Animate();
    }

    private void Animate()
    {
        for (var i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not Ellipse dot || dot.RenderTransform is not TransformGroup { Children: [ScaleTransform scale, TranslateTransform move] }) continue;
            if (!IsVisible)
            {
                dot.BeginAnimation(OpacityProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                continue;
            }
            var delay = TimeSpan.FromSeconds(i * Period * 0.16);
            dot.BeginAnimation(OpacityProperty, Wave(0.3, 1, delay));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Wave(0.8, 1.15, delay));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Wave(0.8, 1.15, delay));
            move.BeginAnimation(TranslateTransform.YProperty, Wave(0, -DotSize * 0.45, delay));
        }
    }

    // Rest, swell up and back, rest: one dot's share of the wave.
    private static DoubleAnimationUsingKeyFrames Wave(double rest, double peak, TimeSpan delay)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        return new DoubleAnimationUsingKeyFrames
        {
            BeginTime = delay,
            Duration = TimeSpan.FromSeconds(Period),
            RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new LinearDoubleKeyFrame(rest, KeyTime.FromPercent(0)),
                new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.3), ease),
                new EasingDoubleKeyFrame(rest, KeyTime.FromPercent(0.6), ease),
                new LinearDoubleKeyFrame(rest, KeyTime.FromPercent(1)),
            },
        };
    }
}

/// <summary>
/// Spins an element (e.g. the refresh icon) while a job runs. When the job finishes it doesn't snap back:
/// it eases out to the end of its turn and settles upright.
/// </summary>
public static class Spin
{
    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.RegisterAttached(
        "IsSpinning", typeof(bool), typeof(Spin), new PropertyMetadata(false, OnChanged));

    public static bool GetIsSpinning(DependencyObject d) => (bool)d.GetValue(IsSpinningProperty);
    public static void SetIsSpinning(DependencyObject d, bool value) => d.SetValue(IsSpinningProperty, value);

    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(0.9);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (el.RenderTransform is not RotateTransform { IsFrozen: false } rotate)
        {
            rotate = new RotateTransform();
            el.RenderTransform = rotate;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var angle = rotate.Angle % 360; // where it is right now, mid-animation included
        if (e.NewValue is true)
        {
            // Wind up into one eased turn, then keep turning at a steady pace.
            var start = new DoubleAnimation(angle, angle + 360, Turn) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } };
            start.Completed += (_, _) =>
            {
                if (GetIsSpinning(el))
                    rotate.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.75)) { RepeatBehavior = RepeatBehavior.Forever });
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, start);
        }
        else
        {
            // Finish the turn it's on (plus a bit more if it's nearly there), slowing to a stop.
            var end = angle > 270 ? 720 : 360;
            var stop = new DoubleAnimation(angle, end, TimeSpan.FromSeconds(0.25 + 0.6 * (end - angle) / 360))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            stop.Completed += (_, _) => { if (!GetIsSpinning(el)) rotate.Angle = 0; };
            rotate.Angle = 0;
            rotate.BeginAnimation(RotateTransform.AngleProperty, stop);
        }
    }
}

/// <summary>
/// A "liquid glass" placeholder for content that's loading: a frosted pane with a bright top edge, and a
/// glossy band of light that sweeps across it. Each pane times its sweep by where it sits in the window,
/// so across a whole skeleton page the shine reads as one wave travelling left to right. Animates only
/// while visible.
/// </summary>
public sealed class GlassBlock : Border
{
    private const double Cycle = 2.2;   // s between waves
    private const double Sweep = 1.0;   // s for the band to cross one pane
    private const double Travel = 0.75; // s for the wave to cross the window

    private readonly TranslateTransform _move = new(-1.5, 0);

    public GlassBlock()
    {
        Background = new LinearGradientBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90);
        BorderBrush = new LinearGradientBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;

        // The shine: a soft wide glow with a brighter core, slanted like light across glass.
        var shine = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.2),
            EndPoint = new Point(1, 0.8),
            RelativeTransform = _move,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.2),
                new GradientStop(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF), 0.38),
                new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF), 0.62),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.8),
            },
        };
        var layer = new Border { Background = shine };
        layer.SetBinding(Border.CornerRadiusProperty, new System.Windows.Data.Binding(nameof(CornerRadius)) { Source = this });
        Child = layer;

        IsVisibleChanged += (_, _) => Animate();
        Loaded += (_, _) => Animate();
    }

    private void Animate()
    {
        if (!IsVisible || !IsLoaded || Window.GetWindow(this) is not { ActualWidth: > 0 } window)
        {
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        // Where the pane is across the window (0 = left edge, 1 = right) decides when the wave reaches it.
        double at;
        try { at = Math.Clamp(TransformToAncestor(window).Transform(new Point(0, 0)).X / window.ActualWidth, 0, 1); }
        catch (InvalidOperationException) { at = 0; }
        var start = at * Travel;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Cycle),
            RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(-1.5, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(-1.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))),
                new EasingDoubleKeyFrame(1.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + Sweep)), ease),
                new DiscreteDoubleKeyFrame(1.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Cycle))),
            },
        });
    }
}

/// <summary>Item lists for skeleton placeholders (what's in them doesn't matter, only how many).</summary>
public static class Placeholders
{
    public static int[] Sections { get; } = [0, 1, 2];
    public static int[] Tiles { get; } = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
    public static int[] Rows { get; } = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
}

/// <summary>Varied widths for skeleton lines, so placeholder rows don't look stamped out: index → width.</summary>
public sealed class SkeletonWidthConverter : System.Windows.Data.IValueConverter
{
    private static readonly double[] Factors = [1.0, 0.72, 0.9, 0.6, 0.82, 0.68, 0.95, 0.76];

    public object Convert(object? value, Type t, object? p, System.Globalization.CultureInfo c) =>
        double.Parse(p?.ToString() ?? "100", System.Globalization.CultureInfo.InvariantCulture) * Factors[(value is int i ? i : 0) % Factors.Length];

    public object ConvertBack(object? value, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}
