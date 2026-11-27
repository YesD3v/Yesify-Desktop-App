using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Yesify.Models;
using Yesify.ViewModels;

namespace Yesify;

/// <summary>
/// Apple Music-style lyrics. While the lyrics are visible and music plays, it runs once per frame from the
/// exact audio position: the light sweeps across each word as it's sung (sung words rise a little on word-timed
/// lines, see <see cref="LyricWordView"/>; line-timed lyrics light the whole line), and instrumental breaks
/// show three dots that fill up and breathe.
/// <para>Focus: the line being sung is sharp and bright; every other line is blurred and dimmed, more
/// the further it is. Scrolling the lyrics yourself clears the blur so you can read them all; two seconds
/// after you stop, it glides back to the line being sung and the focus returns.</para>
/// <para>Moving on: when the next line starts, the view jumps there and each line travels up on its own,
/// one shortly after the other, easing in like a spring (instead of the whole page scrolling as one).</para>
/// </summary>
public sealed class LyricsAnimator
{
    /// <summary>
    /// A word is lit over at most this long. Some sources stretch a line's last word over the instrumental
    /// that follows it, which crawled the light across that word for half a minute.
    /// </summary>
    private static readonly TimeSpan LongestWord = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// After a line's last word, how long it stays lit before dimming when nothing is sung next (an outro,
    /// a long pause); otherwise the last line stayed lit through the whole instrumental ending.
    /// </summary>
    private static readonly TimeSpan LingerAfterLine = TimeSpan.FromSeconds(1.2);
    private static readonly Duration Ease = new(TimeSpan.FromMilliseconds(380));
    private static readonly Duration FocusEase = new(TimeSpan.FromMilliseconds(420));

    // The jump between lines.
    private const double JumpSeconds = 0.78;
    private const double JumpStagger = 0.045; // s between one line and the next
    private const int JumpStaggerLines = 9;

    private readonly MainViewModel _vm;
    private readonly ItemsControl _list;
    private readonly ScrollViewer _scroller;
    private readonly double _anchor;
    private readonly double _blurScale;
    private bool _perFrame;
    private int _active = -1;
    private int _focus = -1; // the line the blur is centred on: the active one, or the last one sung

    /// <summary>How long after the user's last scroll the view goes back to following the song.</summary>
    private static readonly TimeSpan ResyncDelay = TimeSpan.FromSeconds(2);

    private bool _userScrolled;
    private readonly DispatcherTimer _resync;
    private double _wheelTarget;
    private DateTime _wheelUntil;
    private bool _scrollPending;

    /// <param name="anchor">Where the line being sung sits, as a fraction of the view's height.</param>
    /// <param name="blurScale">Blur strength (smaller text needs less).</param>
    public LyricsAnimator(MainViewModel vm, ItemsControl list, ScrollViewer scroller, double anchor = 0.3, double blurScale = 1)
    {
        _vm = vm;
        _anchor = anchor;
        _blurScale = blurScale;
        _list = list;
        _scroller = scroller;
        _vm.Lyrics.CollectionChanged += OnLyricsReplaced;
        _scroller.PreviewMouseWheel += OnWheel;
        _list.IsVisibleChanged += OnListVisibleChanged;
        _resync = new DispatcherTimer(ResyncDelay, DispatcherPriority.Normal, (_, _) => Resync(), _scroller.Dispatcher);
        _resync.Stop();
    }

    private bool _renderQueued, _focusQueued;

    // The active line's word blocks, found once instead of searching the visual tree on every frame.
    private int _rootIdx = -1;
    private FrameworkElement? _root;
    private List<LyricWordView>? _rootWords;
    private bool _unsynced;

    private void OnLyricsReplaced(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Lines arrive a few per frame; only a clear (a new song) starts over.
        _unsynced = _vm.Lyrics.Count > 0 && _vm.Lyrics.All(l => l.Time == null);
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            _rootIdx = -1;
            _root = null;
            _rootWords = null;
            _active = -1; // new song: containers are rebuilt
            _focus = -1;
            _focusState.Clear();
            _jumps.Clear();
            _userScrolled = false;
            _resync.Stop();
            // Back to the top while the list is empty, so the new song never starts at the old one's scroll.
            _scroller.BeginAnimation(ScrollAnimator.OffsetProperty, null);
            _scroller.ScrollToVerticalOffset(0);
        }
        // New lines get their blur once they're built.
        QueueFocus();
        // Paused (or not playing yet) there's no per-frame pass to pick the new lines up: render once they're built.
        if (!_perFrame && _vm.Lyrics.Count > 0 && !_renderQueued)
        {
            _renderQueued = true;
            _list.Dispatcher.BeginInvoke(() =>
            {
                _renderQueued = false;
                Render(_vm.AudioPosition, sweep: true);
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// The user scrolls the lyrics: glide there smoothly (wheel notches add up while it moves), clear the
    /// blur, and stop following the song until they've left it alone for <see cref="ResyncDelay"/>.
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_scroller.ScrollableHeight <= 0) return;
        if (!_userScrolled)
        {
            _userScrolled = true;
            ApplyFocus();
        }
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

    /// <summary>Back to following the song: glide (gently) to the line being sung, and focus on it again.</summary>
    public void Resync()
    {
        _resync.Stop();
        if (!_userScrolled) return;
        _userScrolled = false;
        ApplyFocus();
        if (_vm.Lyrics.All(l => l.Time == null)) return; // unsynced: nothing to go back to
        ScrollTo(_active, TimeSpan.FromMilliseconds(900), new CubicEase { EasingMode = EasingMode.EaseInOut });
    }

    /// <summary>The panel (or window) opened: put the line being sung in view once it's laid out.</summary>
    private void OnListVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            ReleaseLooks();
            return;
        }
        QueueFocus(); // the lines get their blur (and their cached pictures) back
        if (_userScrolled || _scrollPending) return;
        _scrollPending = true;
        _list.LayoutUpdated += OnLayoutUpdated;
    }

    /// <summary>Call whenever playback, visibility, theme or the active line may have changed.</summary>
    public void Refresh()
    {
        // Runs only while its own list is on screen (the side panel, or the cinema view), and not while the
        // window is minimized (it still counts as visible then, but nobody sees it).
        var perFrame = _vm.IsPlaying && _list.IsVisible && Window.GetWindow(_list) is not { WindowState: WindowState.Minimized };
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
        if (_jumping) CompositionTarget.Rendering -= OnJumpFrame;
        _jumping = false;
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
        if (lines.Count == 0 || _unsynced) return; // unsynced: nothing to follow

        var idx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time is not { } t) continue;
            if (Starts(lines[i], t) <= pos + TimeSpan.FromMilliseconds(100)) idx = i;
            else break;
        }
        // Sung, and nothing after it yet (an outro, a long pause): the line dims instead of staying lit
        // through the music. The view stays where it is.
        var finished = idx >= 0 && Ends(lines[idx]) is { } sungUntil && pos > sungUntil + LingerAfterLine;
        var focus = idx;
        if (finished) idx = -1;

        if (idx != _active)
        {
            if (_active >= 0 && _active < lines.Count) SetLine(_active, active: false);
            _active = idx;
            if (idx >= 0) SetLine(idx, active: true);
            if (focus >= 0 || idx >= 0) _focus = Math.Max(focus, idx);
            ApplyFocus();
            if (!_userScrolled && !finished) ScrollTo(idx);
        }
        if (idx < 0 || ActiveRoot(idx) is not { } root) return;

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
        var words = _rootWords ?? Words(root);
        if (_rootWords == null && words.Count > 0) _rootWords = words; // kept once its words are built
        // Each word lights from its own timing (main line and second voice alike). The word being sung has a
        // faint haze: it comes up as the word starts and fades out over half a second once it's done.
        foreach (var view in words)
        {
            if (view.DataContext is not LyricWord w) continue;
            if (!sweep)
            {
                view.SetProgress(1);
                continue;
            }
            var since = (pos - w.Start).TotalSeconds;
            var length = Math.Max(0.05, Length(w).TotalSeconds);
            var p = Math.Clamp(since / length, 0, 1);
            var haze = since <= 0 ? 0 : since < length ? Math.Min(1, since / 0.15) : Math.Max(0, 1 - (since - length) / 0.5);
            view.SetProgress(p, haze * haze * (3 - 2 * haze));
        }
    }

    private static TimeSpan Length(LyricWord w) => w.End - w.Start > LongestWord ? LongestWord : w.End - w.Start;

    /// <summary>When a line lights up: its first sung word for word-timed lines (a line's own start can sit in the silence before it).</summary>
    private static TimeSpan Starts(LyricLine line, TimeSpan time)
    {
        if (!line.IsWordTimed || line.Words.Count == 0) return time;
        // The first word sung, by either voice (a second voice above the line comes first).
        var first = line.Words.Concat(line.BackgroundWords).Min(w => w.Start) - TimeSpan.FromMilliseconds(250);
        return first > time ? first : time;
    }

    /// <summary>When a line is done being sung: its last word (capped), or the line's end when words are estimated.</summary>
    private static TimeSpan? Ends(LyricLine line)
    {
        if (line.IsBreak) return null;
        if (line.IsWordTimed && line.Words.Count > 0)
            return line.Words.Concat(line.BackgroundWords).Max(w => w.Start + Length(w));
        return line.End;
    }

    private void SetLine(int idx, bool active)
    {
        if (Line(idx) is not { } root) return;
        var scale = Animatable(root);
        var to = active ? 1.0 : 0.96;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, Ease) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, Ease) { EasingFunction = ease });
        if (!active)
        {
            // Past lines dim again (the light fades out), like Apple Music.
            foreach (var w in Words(root)) w.Release();
            SetDots(root, elapsed: 0, length: 0);
        }
    }

    // ---------------------------------------------------------------- focus (blur and dim)

    private readonly Dictionary<int, (double Blur, double Opacity, bool Active)> _focusState = new();

    private void QueueFocus()
    {
        if (_focusQueued) return;
        _focusQueued = true;
        _list.Dispatcher.BeginInvoke(() =>
        {
            _focusQueued = false;
            ApplyFocus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Every line's blur and brightness for where the song is: the active line sharp and bright, the others
    /// blurred and dimmed by how far they are from it. All clear while the user is scrolling, or when the
    /// lyrics aren't synced. Only lines whose look changes are touched; new lines take theirs at once.
    /// </summary>
    private void ApplyFocus()
    {
        var count = _vm.Lyrics.Count;
        for (var i = 0; i < count; i++)
        {
            if (Container(i) is not { } container || Line(i) is not { } root) continue;
            double blur, opacity;
            var active = i == _active && !_unsynced;
            if (_unsynced || _focus < 0 && _active < 0)
            {
                // Nothing being sung yet (or no timing): everything readable, the song's start in focus.
                var d0 = _unsynced ? 0 : i;
                blur = _unsynced ? 0 : Math.Min(d0, 4) * 1.8;
                opacity = _unsynced ? 1 : 1 - Math.Min(d0, 5) * 0.08;
            }
            else if (_userScrolled)
            {
                blur = 0;
                opacity = active ? 1 : 0.9;
            }
            else
            {
                var d = Math.Abs(i - _focus);
                blur = active ? 0 : d == 0 ? 1.5 : Math.Min(3.4 + (d - 1) * 1.7, 9);
                opacity = active ? 1 : d == 0 ? 0.85 : Math.Max(0.42, 0.92 - d * 0.11);
            }
            blur *= _blurScale;

            // Far from the line being sung (off screen, or about to be): no blur and no cached picture to keep in
            // memory. Scrolling shows every line clear anyway.
            if (!_unsynced && _focus >= 0 && !_userScrolled && Math.Abs(i - _focus) > KeptLooks)
            {
                if (_focusState.Remove(i)) Plain(container, root);
                continue;
            }
            var known = _focusState.TryGetValue(i, out var was);
            if (known && Math.Abs(was.Blur - blur) < 0.01 && Math.Abs(was.Opacity - opacity) < 0.005 && was.Active == active) continue;
            _focusState[i] = (blur, opacity, active);
            // Only the lines around the one being sung are on screen for long: the rest change at once.
            var near = _focus < 0 || Math.Abs(i - _focus) <= 2;
            SetFocus(container, root, blur, opacity, active, instant: !known || !near);
        }
    }

    /// <summary>
    /// A line's look. Each line is kept as a picture (a bitmap cache) with its blur baked in, so moving and
    /// fading lines costs the GPU almost nothing; only the line being sung, which changes every frame, is
    /// drawn live. Brightness is set on the line's holder, outside the picture, so dimming never redraws it.
    /// </summary>
    private void SetFocus(FrameworkElement container, FrameworkElement root, double blur, double opacity, bool active, bool instant)
    {
        var d = instant ? new Duration(TimeSpan.Zero) : FocusEase;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (root.Opacity < 1) root.BeginAnimation(UIElement.OpacityProperty, null);
        container.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, d) { EasingFunction = ease });
        container.CacheMode = active ? null : _cache ??= NewCache();

        if (root.Effect is not BlurEffect { IsFrozen: false } soft)
        {
            if (blur < 0.05)
            {
                root.Effect = null;
                return;
            }
            soft = new BlurEffect { Radius = instant ? blur : 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            root.Effect = soft;
        }
        if (instant)
        {
            soft.BeginAnimation(BlurEffect.RadiusProperty, null);
            soft.Radius = blur;
            if (blur < 0.05) root.Effect = null;
            return;
        }
        var to = new DoubleAnimation(blur, d) { EasingFunction = ease };
        if (blur < 0.05)
        {
            // Sharp again: drop the effect once it's there, so the line isn't drawn through a blur for nothing.
            to.Completed += (_, _) => { if (root.Effect == soft && soft.Radius < 0.05) root.Effect = null; };
        }
        soft.BeginAnimation(BlurEffect.RadiusProperty, to);
    }

    private BitmapCache? _cache;

    /// <summary>How many lines either side of the one being sung keep their blur and cached picture.</summary>
    private const int KeptLooks = 8;

    /// <summary>A line with no look of its own: no blur, no cached picture, full brightness from its holder.</summary>
    private static void Plain(FrameworkElement container, FrameworkElement root)
    {
        container.BeginAnimation(UIElement.OpacityProperty, null);
        container.Opacity = 1;
        container.CacheMode = null;
        if (root.Effect is BlurEffect soft) soft.BeginAnimation(BlurEffect.RadiusProperty, null);
        root.Effect = null;
    }

    /// <summary>
    /// The list went out of sight (another tab, cinema, the panel closed): drop every line's blur and cached
    /// picture, which otherwise stay in memory (each cached line is a full picture of itself).
    /// </summary>
    private void ReleaseLooks()
    {
        foreach (var i in _focusState.Keys.ToList())
            if (Container(i) is { } container && Line(i) is { } root) Plain(container, root);
        _focusState.Clear();
    }

    /// <summary>One cache setting for every line, at the screen's scale so text stays crisp.</summary>
    private BitmapCache NewCache()
    {
        var cache = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = false, RenderAtScale = VisualTreeHelper.GetDpi(_list).DpiScaleX };
        cache.Freeze();
        return cache;
    }

    // ---------------------------------------------------------------- break dots

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

    // ---------------------------------------------------------------- following the song

    /// <summary>Puts the active line at the anchor (about a third of the way down): a jump, or a glide when it's far.</summary>
    private void ScrollTo(int idx, TimeSpan? duration = null, IEasingFunction? ease = null)
    {
        double target;
        if (idx < 0) target = 0;
        else if (Line(idx) is { IsVisible: true } el)
            target = _scroller.VerticalOffset + el.TransformToAncestor(_scroller).Transform(new Point(0, 0)).Y
                     - CurrentY(idx, _clock.Elapsed.TotalSeconds) - _scroller.ViewportHeight * _anchor;
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
        var delta = target - _scroller.VerticalOffset;
        // Line to line (near, nobody scrolling, nothing gliding): every line travels on its own.
        if (duration == null && idx >= 0 && Math.Abs(delta) <= _scroller.ViewportHeight * 1.1)
        {
            Jump(target, delta, idx);
            return;
        }
        _scroller.BeginAnimation(ScrollAnimator.OffsetProperty,
            new DoubleAnimation(_scroller.VerticalOffset, target, new Duration(duration ?? TimeSpan.FromMilliseconds(650)))
            {
                EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    // Each line's own trip: where it started (as an offset from its place) and when it sets off.
    private readonly Dictionary<int, (double From, double Start)> _jumps = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _jumping;

    /// <summary>
    /// The view moves to <paramref name="target"/> at once, and every line is pushed back to where it was on
    /// screen and then let go, the ones nearest the new line first and each next one a moment later.
    /// </summary>
    private void Jump(double target, double delta, int idx)
    {
        if (Math.Abs(delta) < 0.5) return;
        // A wheel glide still running would fight the jump: settle it where it is.
        var here = _scroller.VerticalOffset;
        ScrollAnimator.SetOffset(_scroller, here);
        _scroller.BeginAnimation(ScrollAnimator.OffsetProperty, null);

        var now = _clock.Elapsed.TotalSeconds;
        var count = _vm.Lyrics.Count;
        var view = _scroller.ViewportHeight;
        for (var i = 0; i < count; i++)
        {
            if (Container(i) is not { } c) continue;
            var y = CurrentY(i, now);
            // Lines that are off screen before and after the move just go to their place.
            var top = c.TranslatePoint(default, _scroller).Y;
            if ((top + c.ActualHeight < -view * 0.5 || top > view * 1.5) && (top - delta + c.ActualHeight < -view * 0.5 || top - delta > view * 1.5))
            {
                if (_jumps.Remove(i)) Shift(c).Y = 0;
                continue;
            }
            // Moving down the song: the line before the new one leads and those below follow. Going back
            // up: the other way round.
            var rank = delta > 0 ? i - (idx - 1) : (idx + 1) - i;
            var delay = Math.Clamp(rank, 0, JumpStaggerLines) * JumpStagger;
            _jumps[i] = (y + delta, now + delay);
            Shift(c).Y = y + delta;
        }
        _scroller.ScrollToVerticalOffset(target);
        if (!_jumping)
        {
            _jumping = true;
            CompositionTarget.Rendering += OnJumpFrame;
        }
    }

    /// <summary>A line's offset from its place right now (0 when it isn't moving).</summary>
    private double CurrentY(int i, double now)
    {
        if (!_jumps.TryGetValue(i, out var j)) return 0;
        var t = (now - j.Start) / JumpSeconds;
        if (t <= 0) return j.From;
        if (t >= 1) return 0;
        return j.From * (1 - Spring(t));
    }

    /// <summary>A critically damped spring, normalised to land exactly at 1: quick to leave, soft to arrive.</summary>
    private static double Spring(double t)
    {
        const double k = 6.5;
        var end = 1 - (1 + k) * Math.Exp(-k);
        return (1 - (1 + k * t) * Math.Exp(-k * t)) / end;
    }

    private readonly List<int> _landed = [];

    private void OnJumpFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        _landed.Clear();
        foreach (var (i, j) in _jumps)
        {
            var y = CurrentY(i, now);
            if (Container(i) is { } c) Shift(c).Y = y;
            if (now - j.Start >= JumpSeconds) _landed.Add(i);
        }
        foreach (var i in _landed) _jumps.Remove(i);
        if (_jumps.Count == 0)
        {
            _jumping = false;
            CompositionTarget.Rendering -= OnJumpFrame;
        }
    }

    private static TranslateTransform Shift(FrameworkElement container)
    {
        if (container.RenderTransform is TranslateTransform { IsFrozen: false } t) return t;
        t = new TranslateTransform();
        container.RenderTransform = t;
        return t;
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
            // (Lines arrive a few per frame, so a line far down can take a while.)
            if (++_layoutTries > 150) StopWaiting();
            return;
        }
        StopWaiting();
        SetLine(_active, active: true);
        ApplyFocus();
        // Opening onto the song: straight there, no travelling.
        if (Line(_active) is { } el)
        {
            var target = _scroller.VerticalOffset + el.TransformToAncestor(_scroller).Transform(new Point(0, 0)).Y - _scroller.ViewportHeight * _anchor;
            ScrollAnimator.SetOffset(_scroller, Math.Clamp(target, 0, _scroller.ScrollableHeight));
            _scroller.BeginAnimation(ScrollAnimator.OffsetProperty, null);
            _scroller.ScrollToVerticalOffset(Math.Clamp(target, 0, _scroller.ScrollableHeight));
        }
    }

    private void StopWaiting()
    {
        _list.LayoutUpdated -= OnLayoutUpdated;
        _scrollPending = false;
        _layoutTries = 0;
    }

    /// <summary>The active line's root, remembered while it stays the active line (not remembered until it's built).</summary>
    private FrameworkElement? ActiveRoot(int idx)
    {
        if (idx == _rootIdx && _root is { IsLoaded: true }) return _root;
        _rootIdx = idx;
        _rootWords = null;
        _root = Line(idx);
        if (_root is not { IsLoaded: true }) _rootIdx = -1; // try again next frame
        return _root;
    }

    private FrameworkElement? Container(int idx) => _list.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;

    private FrameworkElement? Line(int idx) =>
        Container(idx) is { } c ? Find<Grid>(c, g => g.Name == "LineRoot") : null;

    private static List<LyricWordView> Words(DependencyObject root)
    {
        var result = new List<LyricWordView>();
        Collect(root, result);
        return result;

        static void Collect(DependencyObject d, List<LyricWordView> acc)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is LyricWordView w) acc.Add(w);
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
