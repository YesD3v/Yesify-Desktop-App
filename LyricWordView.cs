using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Yesify.Models;

namespace Yesify;

/// <summary>
/// One sung word of the lyrics, like Apple Music: every word sits still on the line, and the light sweeps
/// across it as it's sung (white behind a soft edge, translucent ahead of it). The word being sung has a
/// faint soft haze around it, which fades once it's done.
/// <para>Cheap on purpose: the word is drawn once. Each frame only moves its gradient's stops, which the
/// GPU picks up without the word being drawn again; only the word being sung carries the haze.</para>
/// </summary>
public sealed class LyricWordView : FrameworkElement
{
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(LyricWordView),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits, OnFontChanged));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(LyricWordView),
        new FrameworkPropertyMetadata(FontWeights.Bold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits, OnFontChanged));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(LyricWordView),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits, OnFontChanged));

    /// <summary>How lit the word is shown (1 normally; eases to 0 when its line ends, so the light fades instead of snapping off).</summary>
    public static readonly DependencyProperty LitProperty = DependencyProperty.Register(nameof(Lit), typeof(double), typeof(LyricWordView),
        new PropertyMetadata(1.0, (d, _) => ((LyricWordView)d).Apply()));

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double Lit { get => (double)GetValue(LitProperty); set => SetValue(LitProperty, value); }

    private const double Feather = 0.4;      // the light's soft edge, relative to the word's width
    private const double HazeOpacity = 0.32; // the soft light around the word being sung, at its brightest

    private static readonly Color LitColor = Colors.White;
    private static readonly Color UnlitColor = Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF);
    private static readonly Color BackLitColor = Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF);
    private static readonly Color BackUnlitColor = Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF);

    private LyricWord? _word;
    private FormattedText? _text;
    private readonly LinearGradientBrush _sweep;
    private double _progress;
    private double _haze;
    private bool _fading;

    public LyricWordView()
    {
        Focusable = false;
        _sweep = new LinearGradientBrush(
            [new GradientStop(UnlitColor, 0), new GradientStop(UnlitColor, 0), new GradientStop(UnlitColor, 0), new GradientStop(UnlitColor, 1)],
            new Point(0, 0.5), new Point(1, 0.5));
        DataContextChanged += (_, _) =>
        {
            _word = DataContext as LyricWord;
            _text = null;
            _progress = 0;
            SetHaze(0);
            Apply();
            InvalidateMeasure();
            InvalidateVisual();
        };
    }

    private static void OnFontChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LyricWordView)d)._text = null;

    /// <summary>
    /// The word is lit as far as <paramref name="progress"/> (0 = not yet sung, 1 = done), with the soft haze
    /// around it at <paramref name="haze"/> (0 to 1: up while it's being sung, fading after).
    /// </summary>
    public void SetProgress(double progress, double haze = 0)
    {
        if (_fading)
        {
            _fading = false;
            BeginAnimation(LitProperty, null);
            Lit = 1;
        }
        SetHaze(haze);
        if (Math.Abs(progress - _progress) < 0.001) return;
        _progress = progress;
        Apply();
    }

    /// <summary>The faint light around the word (a soft white shadow of its own letters); gone entirely at 0.</summary>
    private void SetHaze(double haze)
    {
        haze = Math.Round(Math.Clamp(haze, 0, 1), 2);
        if (haze == _haze) return;
        _haze = haze;
        if (haze <= 0)
        {
            Effect = null;
            return;
        }
        if (Effect is not DropShadowEffect { IsFrozen: false } glow)
        {
            Effect = glow = new DropShadowEffect
            {
                Color = Colors.White, ShadowDepth = 0, BlurRadius = FontSize * 0.55, RenderingBias = RenderingBias.Performance,
            };
        }
        glow.Opacity = HazeOpacity * haze;
    }

    /// <summary>The line is over: the light fades out.</summary>
    public void Release()
    {
        SetHaze(0);
        if (_progress <= 0 || _fading) return;
        _fading = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) =>
        {
            if (!_fading) return;
            _fading = false;
            _progress = 0;
            BeginAnimation(LitProperty, null);
            Lit = 1;
            Apply();
        };
        BeginAnimation(LitProperty, fade);
    }

    /// <summary>Moves the light's edge for the current progress (no redraw needed).</summary>
    private void Apply()
    {
        var stops = _sweep.GradientStops;
        var back = _word?.IsBackground == true;
        Color lit = back ? BackLitColor : LitColor, unlit = back ? BackUnlitColor : UnlitColor;
        if (_word is { End.Ticks: 0 }) // unsynced lyrics: plain, nothing to follow
        {
            lit = unlit = back ? Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
            _progress = 1;
        }
        var shown = Lit;
        // Fading out: the lit colour eases back to the unlit one.
        var litNow = Color.FromArgb((byte)(unlit.A + (lit.A - unlit.A) * shown), 0xFF, 0xFF, 0xFF);
        var lead = _progress * (1 + Feather);
        stops[0].Color = litNow;
        stops[1].Color = litNow;
        stops[1].Offset = lead - Feather;
        stops[2].Color = unlit;
        stops[2].Offset = lead;
        stops[3].Color = unlit;
    }

    private FormattedText Text => _text ??= new FormattedText(_word?.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, _sweep, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_word == null) return default;
        var t = Text;
        return new Size(t.WidthIncludingTrailingWhitespace, t.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_word == null) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // clicks between the letters reach the line
        var t = Text;
        // The sweep spans the letters, not the space after them.
        _sweep.MappingMode = BrushMappingMode.Absolute;
        _sweep.StartPoint = new Point(0, 0);
        _sweep.EndPoint = new Point(Math.Max(1, t.Width), 0);
        t.SetForegroundBrush(_sweep);
        dc.DrawText(t, default);
    }
}
