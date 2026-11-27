using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Yesify;

/// <summary>
/// Idle screen: after a while with nothing touched in this window (Settings, Screen saver; never in cinema),
/// the app's panels are taken out of the window altogether and its memory given back to Windows (re-trimmed
/// every 30 s), the backdrop stops moving and the UI refresh stops; a sleepy "Z z z" with the time
/// sits still over the artwork backdrop. If Yesify is the active window it also goes full screen, and steps back
/// out the moment another window takes over, so it never sits on top of a game. Any input wakes it: a short
/// "waking up" shows while the app is laid out again, then everything fades back in.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _idleTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastInput = DateTime.UtcNow;
    private DateTime _idleSince;
    private Point? _lastMouse;
    private bool _idle;
    private bool _waking;
    private bool _idleMadeFullscreen;
    private Panel? _rootHost;
    private int _rootIndex;
    private DateTime _lastIdleTrim;

    private void InitIdle()
    {
        AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(Idle_MouseMove), true);
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(Idle_Input), true);
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(Idle_Input), true);
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Idle_Input), true);
        AddHandler(PreviewStylusDownEvent, new StylusDownEventHandler(Idle_Input), true);
        AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(Idle_Input), true);
        Activated += (_, _) => NoteActivity();
        Deactivated += (_, _) =>
        {
            // Another window took over: never stay full screen and on top of it.
            if (_idle && _idleMadeFullscreen) { _idleMadeFullscreen = false; SetFullscreen(false); }
        };
        _idleTimer.Tick += (_, _) => IdleTick();
        _idleTimer.Start();
    }

    private void Idle_Input(object sender, RoutedEventArgs e)
    {
        // The key or click that wakes the idle screen does only that (Esc shouldn't also leave something).
        if (_idle) e.Handled = true;
        NoteActivity();
    }

    private void Idle_MouseMove(object sender, MouseEventArgs e)
    {
        // WPF also raises mouse moves when things move under a still mouse (the layout changing, the window
        // going full screen): only a real move of the pointer on screen counts.
        Point at;
        try { at = PointToScreen(e.GetPosition(this)); } catch (InvalidOperationException) { return; }
        var moved = _lastMouse is not { } last || Math.Abs(at.X - last.X) + Math.Abs(at.Y - last.Y) > 6;
        _lastMouse = at;
        if (moved) NoteActivity();
    }

    private void NoteActivity()
    {
        _lastInput = DateTime.UtcNow;
        // A moment's grace after dozing off, so the input that happened to land then doesn't wake it at once.
        if (_idle && !_waking && DateTime.UtcNow - _idleSince > TimeSpan.FromMilliseconds(700)) WakeFromIdle();
    }

    private void IdleTick()
    {
        if (_idle)
        {
            UpdateIdleClock();
            if (Vm.IsCinema && !_waking) WakeFromIdle(); // cinema was opened some other way (a hotkey)
            else if (_rootHost != null && DateTime.UtcNow - _lastIdleTrim > TimeSpan.FromSeconds(30))
            {
                // Song changes and downloads touch new memory; the idle screen itself needs very little.
                _lastIdleTrim = DateTime.UtcNow;
                App.TrimWorkingSet();
            }
            return;
        }
        var seconds = Vm.IdleSeconds;
        // Never doze off over the update screen (offered or installing): it would hide it mid-download.
        if (seconds <= 0 || Vm.IsCinema || Vm.IsUpdateScreenOpen || Vm.IsUpdating || !IsLoaded) { _lastInput = DateTime.UtcNow; return; }
        if (DateTime.UtcNow - _lastInput >= TimeSpan.FromSeconds(seconds)) EnterIdle();
    }

    private void UpdateIdleClock() => IdleClock.Text = DateTime.Now.ToShortTimeString();

    /// <summary>For the test harness: go idle now.</summary>
    internal void EnterIdleNow() => EnterIdle();

    private void EnterIdle()
    {
        if (_idle) return;
        _idle = true;
        _waking = false;
        _idleSince = DateTime.UtcNow;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        UpdateIdleClock();
        IdleSleep.Visibility = Visibility.Visible;
        IdleSleep.BeginAnimation(OpacityProperty, null);
        IdleSleep.Opacity = 1;
        IdleWake.BeginAnimation(OpacityProperty, null);
        IdleWake.Opacity = 0;
        IdleWake.Visibility = Visibility.Collapsed;
        IdleScreen.Visibility = Visibility.Visible;
        IdleScreen.IsHitTestVisible = true;
        IdleScreen.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = ease });

        // The app fades away, then is taken out of the layout altogether while nobody's looking.
        Root.IsHitTestVisible = false;
        var hide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)) { EasingFunction = ease };
        hide.Completed += (_, _) =>
        {
            if (!_idle || _waking) return;
            // Out of the window altogether, so the graphics side drops everything it holds for the panels too.
            Root.Visibility = Visibility.Collapsed;
            if (Root.Parent is Panel host)
            {
                _rootHost = host;
                _rootIndex = host.Children.IndexOf(Root);
                host.Children.Remove(Root);
            }
            Vm.SetUiVisible(false);
            UpdateBackdropMotion();
            _lyrics.Refresh();
            _cinemaLyrics.Refresh();
            // Give the memory back now, and again once finalizers have let go of images and handles.
            Dispatcher.BeginInvoke(App.TrimMemory, DispatcherPriority.ApplicationIdle);
            _lastIdleTrim = DateTime.UtcNow;
            var again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            again.Tick += (_, _) => { again.Stop(); if (_idle && !_waking) App.TrimMemory(); };
            again.Start();
        };
        Root.BeginAnimation(OpacityProperty, hide);

        // Full screen only when Yesify is the window in front: never over a game or another app.
        _idleMadeFullscreen = false;
        if (IsActive && WindowState != WindowState.Minimized && !_fullscreen)
        {
            SetFullscreen(true);
            _idleMadeFullscreen = _fullscreen;
        }
    }

    /// <summary>For the test harness: wake now.</summary>
    internal void WakeFromIdleNow() => WakeFromIdle();

    private async void WakeFromIdle()
    {
        if (!_idle || _waking) return;
        _waking = true;
        var started = DateTime.UtcNow;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // "Waking up…" while the app is put back and laid out behind the overlay.
        var sleepOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        sleepOut.Completed += (_, _) => { if (_waking) IdleSleep.Visibility = Visibility.Collapsed; };
        IdleSleep.BeginAnimation(OpacityProperty, sleepOut);
        IdleWake.Visibility = Visibility.Visible;
        IdleWake.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = ease });

        if (_idleMadeFullscreen) { _idleMadeFullscreen = false; SetFullscreen(false); }
        if (_rootHost != null)
        {
            _rootHost.Children.Insert(Math.Min(_rootIndex, _rootHost.Children.Count), Root);
            _rootHost = null;
        }
        Vm.SetUiVisible(true);
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Root.Visibility = Visibility.Visible;
        RepaintAll();
        _lyrics.Refresh();
        _cinemaLyrics.Refresh();

        // Let every tab lay itself out and render once, and show the loader at least briefly so it doesn't flash.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var left = TimeSpan.FromMilliseconds(750) - (DateTime.UtcNow - started);
        if (left > TimeSpan.Zero) await Task.Delay(left);
        if (!_idle) return;

        ScrollQueueToNowPlaying();
        var d = TimeSpan.FromMilliseconds(420);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        var overlayOut = new DoubleAnimation(0, d) { EasingFunction = ease };
        overlayOut.Completed += (_, _) =>
        {
            if (_idle) return; // dozed off again meanwhile
            IdleScreen.Visibility = Visibility.Collapsed;
        };
        IdleScreen.IsHitTestVisible = false;
        Root.IsHitTestVisible = !Vm.IsCinema;
        _idle = false;
        _waking = false;
        _lastInput = DateTime.UtcNow;
        UpdateBackdropMotion();
        IdleScreen.BeginAnimation(OpacityProperty, overlayOut);
    }
}
