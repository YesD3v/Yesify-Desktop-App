using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Yesify.Models;
using Yesify.ViewModels;

namespace Yesify;

/// <summary>
/// Apple Music-style lyrics. In glass mode, while the lyrics are visible and music plays, it runs once per
/// frame from the exact audio position: a soft white gradient sweeps across each word as it's sung (when the
/// lyrics have real per-word timing; line-timed lyrics light the whole line),
/// instrumental breaks show three dots that fill up and breathe, and the view glides to the active line.
/// Otherwise (paused or hidden) it just lights the active line, with no per-frame work.
/// Scrolling the lyrics yourself (mouse wheel, smoothly) stops the view following the song; two seconds
/// after you stop, it glides back to the line being sung.
/// </summary>
public sealed class LyricsAnimator
{
    private static readonly Color Lit = Colors.White;
    private static readonly Color Unlit = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
    private const double Feather = 0.35; // width of the soft edge, relative to the word
    private static readonly Duration Ease = new(TimeSpan.FromMilliseconds(280));

    private readonly MainViewModel _vm;
    private readonly ItemsControl _list;
    private readonly ScrollViewer _scroller;
    private readonly double _anchor;
    private bool _perFrame;
    private int _active = -1;

    /// <summary>How long after the user's last scroll the view goes back to following the song.</summary>
    private static readonly TimeSpan ResyncDelay = TimeSpan.FromSeconds(2);

    private bool _userScrolled;
    private readonly DispatcherTimer _resync;
    private double _wheelTarget;
    private DateTime _wheelUntil;
    private bool _scrollPending;

    /// <param name="anchor">Where the line being sung sits, as a fraction of the view's height.</param>
    public LyricsAnimator(MainViewModel vm, ItemsControl list, ScrollViewer scroller, double anchor = 0.3)
    {
        _vm = vm;
        _anchor = anchor;
        _list = list;
        _scroller = scroller;
        _vm.Lyrics.CollectionChanged += OnLyricsReplaced;
        _scroller.PreviewMouseWheel += OnWheel;
        _list.IsVisibleChanged += OnListVisibleChanged;
        _resync = new DispatcherTimer(ResyncDelay, DispatcherPriority.Normal, (_, _) => Resync(), _scroller.Dispatcher);
        _resync.Stop();
    }

    private void OnLyricsReplaced(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _active = -1; // new song: containers are rebuilt
        _userScrolled = false;
        _resync.Stop();
        // Paused (or not playing yet) there's no per-frame pass to pick the new lines up: render once they're built.
        if (!_perFrame && _vm.Lyrics.Count > 0)
            _list.Dispatcher.BeginInvoke(() => Render(_vm.AudioPosition, sweep: true), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The user scrolls the lyrics: glide there smoothly (wheel notches add up while it moves) and stop
    /// following the song until they've left it alone for <see cref="ResyncDelay"/>.
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_scroller.ScrollableHeight <= 0) return;
        _userScrolled = true;
        _resync.Stop();
        _resync.Start();

        var from = _scroller.VerticalOffset;
        var basis = DateTime.UtcNow < _wheelUntil ? _wheelTarget : from;
        _wheelTarget = Math.Clamp(basis - e.Delta * 1.1, 0, _scroller.ScrollableHeight);
        var duration = TimeSpan.FromMilliseconds(320);
        _wheelUntil = DateTime.UtcNow + duration;
        _scroller.BeginAnimation(ScrollAnimator.OffsetProperty,
            new DoubleAnimation(from, _wheelTarget, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Back to following the song: glide (gently) to the line being sung.</summary>
    public void Resync()
    {
        _resync.Stop();
        if (!_userScrolled) return;
        _userScrolled = false;
        if (_vm.Lyrics.All(l => l.Time == null)) return; // unsynced: nothing to go back to
        ScrollTo(_active, TimeSpan.FromMilliseconds(900), new CubicEase { EasingMode = EasingMode.EaseInOut });
    }

    /// <summary>The panel (or window) opened: put the line being sung in view once it's laid out.</summary>
    private void OnListVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || _userScrolled || _scrollPending) return;
        _scrollPending = true;
        _list.LayoutUpdated += OnLayoutUpdated;
    }

    /// <summary>A fresh brush per word (the sweep moves its gradient stops).</summary>
    public static Brush NewWordBrush(LyricWord word)
    {
        if (word.End == TimeSpan.Zero) return new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)); // unsynced lyrics
        return new LinearGradientBrush(
            [new GradientStop(Lit, 0), new GradientStop(Lit, -Feather), new GradientStop(Unlit, 0), new GradientStop(Unlit, 1)],
            new Point(0, 0.5), new Point(1, 0.5));
    }

    /// <summary>Call whenever playback, visibility, theme or the active line may have changed.</summary>
    public void Refresh()
    {
        // Runs only while its own list is on screen (the side panel, or the cinema view).
        var perFrame = _vm.IsPlaying && _list.IsVisible;
        if (perFrame != _perFrame)
        {
            if (perFrame) CompositionTarget.Rendering += OnFrame;
            else CompositionTarget.Rendering -= OnFrame;
            _perFrame = perFrame;
        }
        if (!perFrame)
        {
            // Paused (or hidden): the audible position, for an exact still frame.
            Render(_vm.AudioPosition, sweep: true);
        }
    }

    public void Stop()
    {
        if (_perFrame) CompositionTarget.Rendering -= OnFrame;
        _perFrame = false;
        _vm.Lyrics.CollectionChanged -= OnLyricsReplaced;
        _scroller.PreviewMouseWheel -= OnWheel;
        _list.IsVisibleChanged -= OnListVisibleChanged;
        _list.LayoutUpdated -= OnLayoutUpdated;
        _resync.Stop();
    }

    private void OnFrame(object? sender, EventArgs e) => Render(_vm.AudioPosition, sweep: true);

    private void Render(TimeSpan pos, bool sweep)
    {
        var lines = _vm.Lyrics;
        if (lines.Count == 0 || lines.All(l => l.Time == null)) return; // unsynced: nothing to follow

        var idx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time is not { } t) continue;
            if (t <= pos + TimeSpan.FromMilliseconds(100)) idx = i;
            else break;
        }

        if (idx != _active)
        {
            if (_active >= 0 && _active < lines.Count) SetLine(_active, active: false);
            _active = idx;
            if (idx >= 0) SetLine(idx, active: true);
            if (!_userScrolled) ScrollTo(idx);
        }
        if (idx < 0 || Line(idx) is not { } root) return;

        var line = lines[idx];
        if (line.IsBreak)
        {
            var start = line.Time ?? TimeSpan.Zero;
            var end = line.End ?? start + TimeSpan.FromSeconds(5);
            SetDots(root, (pos - start).TotalSeconds, (end - start).TotalSeconds);
            return;
        }

        // Only sweep word by word when the source timed each word; guessed timing would drift, so the whole line lights up.
        sweep &= line.IsWordTimed;
        var words = Words(root);
        for (var i = 0; i < words.Count && i < line.Words.Count; i++)
        {
            var w = line.Words[i];
            var p = sweep ? Math.Clamp((pos - w.Start).TotalSeconds / Math.Max(0.05, (w.End - w.Start).TotalSeconds), 0, 1) : 1;
            SetWord(words[i], p);
        }
    }

    // Lit part grows from the left with a feathered edge: p=0 all unlit, p=1 all lit.
    private static void SetWord(TextBlock tb, double p)
    {
        if (tb.Foreground is not LinearGradientBrush { IsFrozen: false } b || b.GradientStops.Count < 4) return;
        var lead = p * (1 + Feather);
        b.GradientStops[1].Offset = lead - Feather;
        b.GradientStops[2].Offset = lead;
    }

    private void SetLine(int idx, bool active)
    {
        if (Line(idx) is not { } root) return;
        var scale = Animatable(root);
        {
            var to = active ? 1.0 : 0.96;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, Ease) { EasingFunction = new CubicEase() });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, Ease) { EasingFunction = new CubicEase() });
        }
        if (!active)
        {
            // Past lines dim again, like Apple Music.
            foreach (var w in Words(root)) SetWord(w, 0);
            SetDots(root, elapsed: 0, length: 0);
        }
    }

    private const double DotLift = 3;      // px a fully lit dot rises
    private const double DotFadeIn = 0.35; // s
    private const double DotFadeOut = 0.6; // s, before the next line

    /// <summary>
    /// Instrumental break: a soft light travels across the three dots over the break. Each dot's height,
    /// glow and size follow the light on it: the dot under the peak rises highest and brightest, dots the
    /// light has passed keep a faint glow and settle low, dots it hasn't reached rest at the bottom. The
    /// dots fade in at the start and shrink away just before the next line. Everything is a pure function
    /// of the playback position, so it's smooth and exactly in time.
    /// </summary>
    private static void SetDots(FrameworkElement root, double elapsed, double length)
    {
        var dots = Find<StackPanel>(root, sp => sp.Children.Count == 3 && sp.Children[0] is System.Windows.Shapes.Ellipse);
        if (dots == null) return;

        if (length <= 0)
        {
            // Not the active line: resting.
            dots.Opacity = 1;
            for (var i = 0; i < 3; i++) Pose(dots.Children[i], light: 0, glow: 0);
            return;
        }

        // The light finishes crossing as the fade-out begins.
        var travel = Math.Max(0.1, length - DotFadeOut);
        var front = Math.Clamp(elapsed / travel, 0, 1) * 3; // 0 → 3 across the dots
        for (var i = 0; i < 3; i++)
        {
            var d = front - (i + 0.5);
            var peak = Math.Exp(-d * d / (2 * 0.38 * 0.38)); // the light passing over this dot
            var passed = SmoothStep(Math.Clamp(front - i, 0, 1));
            Pose(dots.Children[i], light: Math.Max(peak, 0.3 * passed), glow: peak);
        }

        var fadeIn = SmoothStep(Math.Clamp(elapsed / DotFadeIn, 0, 1));
        var fadeOut = SmoothStep(Math.Clamp((length - elapsed) / DotFadeOut, 0, 1));
        dots.Opacity = Math.Min(fadeIn, fadeOut);
        var group = Animatable(dots);
        group.ScaleX = group.ScaleY = 0.6 + 0.4 * fadeOut; // shrink away into the next line
    }

    private static void Pose(UIElement dot, double light, double glow)
    {
        dot.Opacity = 0.28 + 0.72 * light;
        if (dot.RenderTransform is not TransformGroup { IsFrozen: false, Children: [ScaleTransform s, TranslateTransform t] })
        {
            s = new ScaleTransform();
            t = new TranslateTransform();
            dot.RenderTransformOrigin = new Point(0.5, 0.5);
            dot.RenderTransform = new TransformGroup { Children = { s, t } };
        }
        t.Y = -DotLift * light;
        s.ScaleX = s.ScaleY = 1 + 0.22 * glow;
    }

    private static double SmoothStep(double x) => x * x * (3 - 2 * x);

    /// <summary>Transforms declared in templates are frozen; swap in a private, changeable copy once.</summary>
    private static ScaleTransform Animatable(UIElement el)
    {
        if (el.RenderTransform is ScaleTransform { IsFrozen: false } live) return live;
        var current = el.RenderTransform as ScaleTransform;
        var fresh = new ScaleTransform(current?.ScaleX ?? 1, current?.ScaleY ?? 1);
        el.RenderTransform = fresh;
        return fresh;
    }

    /// <summary>Glides so the active line sits at the anchor (about a third of the way down).</summary>
    private void ScrollTo(int idx, TimeSpan? duration = null, IEasingFunction? ease = null)
    {
        double target;
        if (idx < 0) target = 0;
        else if (Line(idx) is { IsVisible: true } el)
            target = _scroller.VerticalOffset + el.TransformToAncestor(_scroller).Transform(new Point(0, 0)).Y - _scroller.ViewportHeight * _anchor;
        else
        {
            // Lines not laid out yet (lyrics just arrived, or the panel just opened): scroll once they are.
            if (!_scrollPending && _list.IsVisible)
            {
                _scrollPending = true;
                _list.LayoutUpdated += OnLayoutUpdated;
            }
            return;
        }

        target = Math.Clamp(target, 0, _scroller.ScrollableHeight);
        _scroller.BeginAnimation(ScrollAnimator.OffsetProperty,
            new DoubleAnimation(_scroller.VerticalOffset, target, new Duration(duration ?? TimeSpan.FromMilliseconds(550)))
            {
                EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private int _layoutTries;

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_userScrolled || _active < 0)
        {
            StopWaiting();
            return;
        }
        if (Line(_active) is not { IsVisible: true })
        {
            // The rows for freshly loaded lyrics aren't built on the first pass: try the next ones.
            if (++_layoutTries > 30) StopWaiting();
            return;
        }
        StopWaiting();
        SetLine(_active, active: true);
        ScrollTo(_active);
    }

    private void StopWaiting()
    {
        _list.LayoutUpdated -= OnLayoutUpdated;
        _scrollPending = false;
        _layoutTries = 0;
    }

    private FrameworkElement? Line(int idx) =>
        _list.ItemContainerGenerator.ContainerFromIndex(idx) is DependencyObject c ? Find<Grid>(c, g => g.Name == "LineRoot") : null;

    private static List<TextBlock> Words(DependencyObject root)
    {
        var result = new List<TextBlock>();
        Collect(root, result);
        return result;

        static void Collect(DependencyObject d, List<TextBlock> acc)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is TextBlock { DataContext: LyricWord } tb) acc.Add(tb);
                else Collect(c, acc);
            }
        }
    }

    private static T? Find<T>(DependencyObject d, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t && match(t)) return t;
            if (Find(c, match) is { } r) return r;
        }
        return null;
    }
}

/// <summary>ScrollViewer offsets can't be animated directly; this attached property can.</summary>
public static class ScrollAnimator
{
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(ScrollAnimator),
        new PropertyMetadata(0.0, (d, e) => (d as ScrollViewer)?.ScrollToVerticalOffset((double)e.NewValue)));

    public static double GetOffset(DependencyObject d) => (double)d.GetValue(OffsetProperty);
    public static void SetOffset(DependencyObject d, double value) => d.SetValue(OffsetProperty, value);
}
