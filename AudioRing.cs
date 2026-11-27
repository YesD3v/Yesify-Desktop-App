using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Yesify;

/// <summary>
/// Home's visualizer: a ring of lines around a circle that move with what Yesify is playing (the spectrum of
/// the sound coming out of the speakers, lows at the top, highs at the bottom, mirrored left and right).
/// Paused, the lines breathe slowly. The element given as <see cref="PulseElement"/> (the cover in the
/// middle) swells a little on the beat.
/// <para><see cref="Soft"/> draws the big background version: fat rounded bars, smoothed across neighbours
/// and over time, with a smooth thick line running around their tips.</para>
/// <para>Drawn by the graphics card (Shaders/AudioRing.fx): each frame the app only hands it a strip of 264
/// levels and a few numbers, so nothing is redrawn or re-shaped on the computer's side. Without shader
/// support it falls back to drawing the lines itself. Only runs while it's on screen and <see cref="IsActive"/>.</para>
/// </summary>
public sealed class AudioRing : FrameworkElement
{
    /// <summary>Where the levels come from (set once by the window): fills the bands, false when nothing plays.</summary>
    public static Func<float[], bool>? Source { get; set; }

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(AudioRing),
        new PropertyMetadata(true, (d, _) => ((AudioRing)d).UpdateRunning()));

    public static readonly DependencyProperty PulseElementProperty = DependencyProperty.Register(nameof(PulseElement), typeof(UIElement), typeof(AudioRing));

    public static readonly DependencyProperty InnerRadiusProperty = DependencyProperty.Register(nameof(InnerRadius), typeof(double), typeof(AudioRing),
        new FrameworkPropertyMetadata(80.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SoftProperty = DependencyProperty.Register(nameof(Soft), typeof(bool), typeof(AudioRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public UIElement? PulseElement { get => (UIElement?)GetValue(PulseElementProperty); set => SetValue(PulseElementProperty, value); }
    public double InnerRadius { get => (double)GetValue(InnerRadiusProperty); set => SetValue(InnerRadiusProperty, value); }
    public bool Soft { get => (bool)GetValue(SoftProperty); set => SetValue(SoftProperty, value); }

    private const int Bands = 36;     // per side
    private const int Lines = Bands * 2;
    private readonly float[] _raw = new float[Bands];
    private readonly double[] _level = new double[Bands];
    private readonly double[] _spread = new double[Bands];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last, _energy, _floor = 0.25;
    private TimeSpan _lastFrame;
    private bool _running;

    public AudioRing()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            BuildLayers();
            UpdateRunning();
        };
        Unloaded += (_, _) => UpdateRunning();
        IsVisibleChanged += (_, _) => UpdateRunning();
    }

    private void UpdateRunning()
    {
        var run = IsActive && IsVisible && IsLoaded;
        if (run == _running) return;
        _running = run;
        if (run) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Once per frame the screen shows (this event can fire more than once for the same frame). It must not
        // skip frames on its own: a frame where nothing changes throws WPF's frame timing off, which stutters.
        if (e is RenderingEventArgs { RenderingTime: var frame })
        {
            if (frame == _lastFrame) return;
            _lastFrame = frame;
        }
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _last, 0.1);
        _last = now;

        var soft = Soft;
        var live = Source?.Invoke(_raw) == true;
        double energy = 0;
        for (var b = 0; b < Bands; b++)
        {
            // Paused: a slow, gentle breathing wave instead of the music.
            var target = live ? _raw[b] : 0.06 + 0.05 * Math.Sin(now * 1.3 + b * 0.45);
            // Quick attack, soft release (softer still for the background ring).
            var k = target > _level[b] ? 1 - Math.Exp(-dt * (soft ? 12 : 28)) : 1 - Math.Exp(-dt * (soft ? 3.5 : 7));
            _level[b] += (target - _level[b]) * k;
            if (b < 6) energy += _level[b] / 6;
        }
        if (soft)
        {
            // Each bar leans on its neighbours, so the shape rolls instead of spiking.
            for (var b = 0; b < Bands; b++)
            {
                double sum = 0, w = 0;
                for (var o = -3; o <= 3; o++)
                {
                    var j = Math.Clamp(b + o, 0, Bands - 1);
                    var weight = 1.0 / (1 + o * o * 0.5);
                    sum += _level[j] * weight;
                    w += weight;
                }
                _spread[b] = sum / w;
            }
        }
        // The beat: lows above their recent average.
        _floor += (energy - _floor) * (1 - Math.Exp(-dt * 1.5));
        var beat = Math.Clamp((energy - _floor) * 3.2, 0, 1);
        _energy += (beat - _energy) * (beat > _energy ? 1 - Math.Exp(-dt * 30) : 1 - Math.Exp(-dt * 6));
        if (PulseElement is { } pulse)
        {
            if (pulse.RenderTransform is not ScaleTransform { IsFrozen: false } s)
            {
                pulse.RenderTransform = s = new ScaleTransform();
                pulse.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            s.ScaleX = s.ScaleY = 1 + 0.05 * _energy;
        }
        if (_bars != null) Upload();
        else InvalidateVisual();
    }

    /// <summary>Where the bars start and how far a full one reaches, for this size.</summary>
    private (double R0, double Reach) Geometry(Size size)
    {
        var soft = Soft;
        var r0 = InnerRadius * (1 + (soft ? 0.03 : 0.05) * _energy) + (soft ? 0 : 10);
        var reach = Math.Max(10, Math.Min(size.Width, size.Height) / 2 - r0 - (soft ? 50 : 4));
        return (r0, reach);
    }

    /// <summary>Bar <paramref name="i"/>'s level (0..71 clockwise from the top; mirrored, lows at the top).</summary>
    private double Bar(int i)
    {
        i = ((i % Lines) + Lines) % Lines;
        var band = i < Bands ? i : Lines - 1 - i;
        return Math.Clamp((Soft ? _spread : _level)[band], 0, 1);
    }

    // ---------------------------------------------------------------- drawn by the graphics card

    private const int Strip = 256, Pad = 4, StripWidth = Strip + 2 * Pad;
    private static PixelShader? _barsShader, _lineShader;
    private ShaderLayer? _bars, _line;
    private WriteableBitmap? _levels;
    private readonly byte[] _pixels = new byte[StripWidth * 4];

    private void BuildLayers()
    {
        if (_bars != null || !RenderCapability.IsPixelShaderVersionSupported(2, 0)) return;
        try
        {
            _barsShader ??= LoadShader("AudioRingBars.ps");
            _lineShader ??= LoadShader("AudioRingLine.ps");
        }
        catch
        {
            return; // no shaders: the lines are drawn the old way
        }
        _levels = new WriteableBitmap(StripWidth, 1, 96, 96, PixelFormats.Bgra32, null);
        var brush = new ImageBrush(_levels) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
        _bars = new ShaderLayer(new RingEffect(_barsShader, brush));
        AddVisualChild(_bars);
        if (Soft)
        {
            _line = new ShaderLayer(new RingEffect(_lineShader, brush));
            AddVisualChild(_line);
        }
        InvalidateMeasure();
        InvalidateVisual();
        Upload();
    }

    private static PixelShader LoadShader(string name)
    {
        var shader = new PixelShader { UriSource = new Uri($"pack://application:,,,/Yesify;component/Shaders/{name}") };
        shader.Freeze();
        return shader;
    }

    /// <summary>Hands this frame to the shaders: the smoothed level all around, and where everything sits.</summary>
    private void Upload()
    {
        if (_bars == null || _levels == null) return;
        // The strip covers -0.5..0.5 turns, with a few wrapped texels each side so its edges never blur in.
        for (var j = 0; j < StripWidth; j++)
        {
            var turns = (j + 0.5 - Pad) / Strip - 0.5;
            var f = turns * Lines - 0.5; // in bars: bar i sits at i
            var i = (int)Math.Floor(f);
            var x = f - i;
            double p0 = Bar(i - 1), p1 = Bar(i), p2 = Bar(i + 1), p3 = Bar(i + 2);
            // Catmull-Rom through the bars: a smooth curve that still passes through each bar's level.
            var v = 0.5 * (2 * p1 + (-p0 + p2) * x + (2 * p0 - 5 * p1 + 4 * p2 - p3) * x * x + (-p0 + 3 * p1 - 3 * p2 + p3) * x * x * x);
            v = Math.Clamp(v, 0, 0.9999) * 255;
            var hi = Math.Floor(v);
            _pixels[j * 4] = 0;
            _pixels[j * 4 + 1] = (byte)Math.Round((v - hi) * 255); // green: the fine part
            _pixels[j * 4 + 2] = (byte)hi;                         // red: the coarse part
            _pixels[j * 4 + 3] = 255;
        }
        _levels.WritePixels(new Int32Rect(0, 0, StripWidth, 1), _pixels, StripWidth * 4, 0);

        var k = VisualTreeHelper.GetDpi(this).DpiScaleX; // the shaders work in screen pixels
        var size = RenderSize;
        var (r0, reach) = Geometry(size);
        var soft = Soft;
        var geo = new Point4D(size.Width * k, size.Height * k, r0 * k, reach * k);
        var bar = soft
            ? new Point4D(5.5 * k, 6 * k, 0.07, 0.26)
            : new Point4D(1.6 * k, 3 * k, 0.27, 0.69);
        var line = new Point4D(16 * k, 3 * k, 0.36, 24 * k);
        var extra = new Point4D(soft ? 0.1 : 0, soft ? 0 : (r0 - 5) * k, soft ? 0 : 0.19, Lines);
        var tex = new Point4D((double)Strip / StripWidth, (Pad + 0.5 * Strip) / StripWidth, 0, 0);
        foreach (var layer in Layers())
        {
            var fx = layer.Ring;
            fx.Geo = geo;
            fx.Bar = bar;
            fx.Line = line;
            fx.Extra = extra;
            fx.Tex = tex;
        }
    }

    private IEnumerable<ShaderLayer> Layers()
    {
        if (_bars != null) yield return _bars;
        if (_line != null) yield return _line;
    }

    protected override int VisualChildrenCount => (_bars != null ? 1 : 0) + (_line != null ? 1 : 0);

    protected override Visual GetVisualChild(int index) => index == 0 ? _bars! : _line!;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var layer in Layers()) layer.Measure(availableSize);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var layer in Layers()) layer.Arrange(new Rect(finalSize));
        if (_bars != null) Upload();
        return finalSize;
    }

    /// <summary>An empty area the size of the ring, which its shader paints.</summary>
    private sealed class ShaderLayer : FrameworkElement
    {
        public RingEffect Ring { get; }

        public ShaderLayer(RingEffect effect)
        {
            Ring = effect;
            Effect = effect;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
    }

    /// <summary>The shader's inputs (see Shaders/AudioRing.fx for what each number means).</summary>
    private sealed class RingEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(RingEffect), 0);
        public static readonly DependencyProperty LevelsProperty = RegisterPixelShaderSamplerProperty("Levels", typeof(RingEffect), 1, SamplingMode.Bilinear);
        public static readonly DependencyProperty GeoProperty = Constant(nameof(Geo), 0);
        public static readonly DependencyProperty BarProperty = Constant(nameof(Bar), 1);
        public static readonly DependencyProperty LineProperty = Constant(nameof(Line), 2);
        public static readonly DependencyProperty ExtraProperty = Constant(nameof(Extra), 3);
        public static readonly DependencyProperty TexProperty = Constant(nameof(Tex), 4);

        private static DependencyProperty Constant(string name, int register) => DependencyProperty.Register(name, typeof(Point4D), typeof(RingEffect),
            new UIPropertyMetadata(default(Point4D), PixelShaderConstantCallback(register)));

        public RingEffect(PixelShader shader, Brush levels)
        {
            PixelShader = shader;
            SetValue(LevelsProperty, levels);
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(LevelsProperty);
            foreach (var p in new[] { GeoProperty, BarProperty, LineProperty, ExtraProperty, TexProperty }) UpdateShaderValue(p);
        }

        public Point4D Geo { set => Set(GeoProperty, value); }
        public Point4D Bar { set => Set(BarProperty, value); }
        public Point4D Line { set => Set(LineProperty, value); }
        public Point4D Extra { set => Set(ExtraProperty, value); }
        public Point4D Tex { set => Set(TexProperty, value); }

        private void Set(DependencyProperty p, Point4D value)
        {
            if ((Point4D)GetValue(p) != value) SetValue(p, value);
        }
    }

    // ---------------------------------------------------------------- fallback: drawn here

    private static readonly Pen[] Pens = MakePens(3.2, 70, 11);
    private static readonly Pen[] SoftPens = MakePens(11, 18, 4);
    private static readonly Pen RingPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1.2));
    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF)), 6) { LineJoin = PenLineJoin.Round });
    private readonly Point[] _tips = new Point[Lines];

    protected override void OnRender(DrawingContext dc)
    {
        if (_bars != null) return; // the shaders draw it
        var size = RenderSize;
        var c = new Point(size.Width / 2, size.Height / 2);
        var soft = Soft;
        var (r0, reach) = Geometry(size);

        if (!soft) dc.DrawEllipse(null, RingPen, c, r0 - 5, r0 - 5);
        var pens = soft ? SoftPens : Pens;
        for (var i = 0; i < Lines; i++)
        {
            var v = Bar(i);
            var angle = (i + 0.5) / Lines * 2 * Math.PI - Math.PI / 2;
            var (sin, cos) = Math.SinCos(angle);
            var len = (soft ? 6 : 3) + v * reach;
            var a = new Point(c.X + cos * r0, c.Y + sin * r0);
            var b = new Point(c.X + cos * (r0 + len), c.Y + sin * (r0 + len));
            dc.DrawLine(pens[(int)Math.Round(v * 16)], a, b);
            if (soft)
            {
                var o = r0 + len + 16;
                _tips[i] = new Point(c.X + cos * o, c.Y + sin * o);
            }
        }
        if (!soft) return;

        // A smooth closed curve around the bars' tips (Catmull-Rom through them).
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(_tips[0], false, true);
            for (var i = 0; i < Lines; i++)
            {
                Point p0 = _tips[(i - 1 + Lines) % Lines], p1 = _tips[i], p2 = _tips[(i + 1) % Lines], p3 = _tips[(i + 2) % Lines];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                g.BezierTo(c1, c2, p2, true, true);
            }
        }
        outline.Freeze();
        dc.DrawGeometry(null, OutlinePen, outline);
    }

    /// <summary>Round-capped white pens from faint to bright (17 steps).</summary>
    private static Pen[] MakePens(double thickness, int alpha, int step) => Enumerable.Range(0, 17).Select(i =>
        Frozen(new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, alpha + i * step), 0xFF, 0xFF, 0xFF)), thickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        })).ToArray();

    private static Pen Frozen(Pen p)
    {
        p.Freeze();
        return p;
    }
}
