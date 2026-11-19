using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Yesify;

/// <summary>
/// Smooth mouse-wheel scrolling for a ScrollViewer (or a ListBox, via its ScrollViewer). Each notch moves
/// a target; the view glides after it every frame, easing out, so scrolling never jumps in 48 px steps.
/// Quick spins speed up (up to 3× per notch) and add up, so a long list is still fast to get through,
/// and the glide catches up within ~150 ms so the view never feels like it lags behind the wheel.
/// Scrolling some other way (scrollbar, keyboard, code) is picked up and the glide starts from there.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(Glide), typeof(SmoothScroll));

    private const double NotchPixels = 110;    // per 120 wheel units
    private const double CatchUp = 16;         // 1/s: how quickly the view closes the gap (higher = snappier)
    private const double MaxBoost = 3;         // fast spins move up to this many times further per notch

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (e.NewValue is true) el.PreviewMouseWheel += OnWheel;
        else el.PreviewMouseWheel -= OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject host || Viewer(host) is not { } sv || sv.ScrollableHeight <= 0) return;
        // A scrollable area inside this one (none today) would get the wheel first.
        e.Handled = true;
        var glide = (Glide?)sv.GetValue(StateProperty);
        if (glide == null)
        {
            glide = new Glide(sv);
            sv.SetValue(StateProperty, glide);
        }
        glide.Wheel(e.Delta);
    }

    private static ScrollViewer? Viewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (Viewer(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private sealed class Glide
    {
        private readonly ScrollViewer _sv;
        private readonly Stopwatch _clock = new();
        private double _position, _target;
        private bool _running;
        private long _lastWheelTicks;
        private double _boost = 1;

        public Glide(ScrollViewer sv)
        {
            _sv = sv;
            _position = _target = sv.VerticalOffset;
            sv.ScrollChanged += OnScrollChanged;
            sv.PreviewMouseDown += (_, _) => Stop(); // grabbing the scrollbar (or clicking) ends the glide
        }

        public void Wheel(int delta)
        {
            // Notches close together (a quick spin) go further each, so long lists stay fast to scroll.
            var now = Environment.TickCount64;
            var gap = now - _lastWheelTicks;
            _lastWheelTicks = now;
            _boost = gap < 60 ? Math.Min(MaxBoost, _boost + 0.35) : gap < 140 ? Math.Max(1, _boost) : 1;

            if (!_running) _position = _target = _sv.VerticalOffset;
            // Reversing direction starts from where the view is, not where it was heading.
            var down = delta < 0;
            if (_running && (down ? _target < _position : _target > _position)) _target = _position;
            _target = Math.Clamp(_target - delta / 120.0 * NotchPixels * _boost, 0, _sv.ScrollableHeight);
            Start();
        }

        private void Start()
        {
            if (_running) return;
            _running = true;
            _clock.Restart();
            CompositionTarget.Rendering += OnFrame;
        }

        private void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var dt = Math.Min(0.05, _clock.Elapsed.TotalSeconds);
            _clock.Restart();
            _target = Math.Clamp(_target, 0, _sv.ScrollableHeight); // the list can shrink while gliding
            var step = (_target - _position) * (1 - Math.Exp(-CatchUp * dt));
            _position += step;
            if (Math.Abs(_target - _position) < 0.5)
            {
                _position = _target;
                Stop();
            }
            _sv.ScrollToVerticalOffset(_position);
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Moved by something else (scrollbar, keyboard, a jump to the playing song): follow it.
            if (!_running && Math.Abs(_sv.VerticalOffset - _position) > 1) _position = _target = _sv.VerticalOffset;
        }
    }
}
