using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Yesify.Services;
using Yesify.ViewModels;

namespace Yesify;

/// <summary>
/// Owns everything that outlives the windows: the view model (and with it the player and queue) and the
/// tray icon. The full window and the miniplayer are created on demand and thrown away when closed;
/// with both closed, only the audio keeps running and memory is trimmed.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private MainViewModel? _vm;
    private MediaSession? _media;
    private DiscordPresence? _discord;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MiniPlayerWindow? _mini;
    private DispatcherTimer? _trimTimer;
    // Memory upkeep, see Maintain().
    private DispatcherTimer? _maintenance;
    private long _lastInputTicks = Environment.TickCount64;
    private long _lastTrimTicks;
    private bool _inputSinceTrim = true;
    private bool _exiting;
    private bool _switchingTheme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);


        // `Yesify.exe --exit` asks a running Yesify to save and quit (used by the launcher before updating).
        if (e.Args.Contains("--exit"))
        {
            var running = TrayIcon.FindWindow(null, TrayIcon.WindowTitle);
            if (running != IntPtr.Zero) TrayIcon.PostMessage(running, TrayIcon.WM_EXIT_REQUEST, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        // One Yesify at a time: a second launch just brings the running one forward.
        _singleInstance = new Mutex(true, TrayIcon.InstanceName, out var isFirst);
        if (!isFirst)
        {
            var host = TrayIcon.FindWindow(null, TrayIcon.WindowTitle);
            if (host != IntPtr.Zero)
            {
                AllowSetForegroundWindow(-1); // ASFW_ANY: let the running instance take focus
                TrayIcon.PostMessage(host, TrayIcon.WM_SHOW_REQUEST, IntPtr.Zero, IntPtr.Zero);
            }
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _vm = new MainViewModel();
        ApplyTheme(_vm.IsLightweight);
        _vm.MiniPlayerRequested += ShowMiniPlayer;
        _vm.FullPlayerRequested += ShowMainWindow;
        _tray = new TrayIcon { IsPlaying = () => _vm.IsAudioPlaying };
        _tray.ShowRequested += ShowMainWindow;
        _tray.MiniPlayerRequested += ShowMiniPlayer;
        _tray.PlayPauseRequested += () => _vm.TogglePlayCommand.Execute(null);
        _tray.NextRequested += () => _vm.NextCommand.Execute(null);
        _tray.PreviousRequested += () => _vm.PreviousCommand.Execute(null);
        _tray.ExitRequested += ExitApp;
        // What's playing shows in Windows' media flyout (and to apps that read media sessions).
        _media = MediaSession.TryCreate(_tray.Handle, _vm);
        _discord = new DiscordPresence(_vm);
        _vm.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(MainViewModel.CurrentTrack)) UpdateTooltip();
            if (a.PropertyName == nameof(MainViewModel.IsLightweight)) SwitchTheme();
        };
        SessionEnding += (_, _) => ExitApp();

        // `--tray` starts without a window (e.g. for autostart); the rest is a search or link to open.
        var args = e.Args.ToList();
        var trayOnly = args.Remove("--tray");
        if (!trayOnly) ShowMainWindow();
        else AfterWindowClosed(); // same background mode as after closing the window
        StartMaintenance();

        // `Yesify.exe <search or link>` opens straight to it.
        if (args.Count > 0)
        {
            _vm.SearchText = string.Join(" ", args);
            _ = _vm.SearchCommand.ExecuteAsync(null);
        }
    }

    // ---------------------------------------------------------------- windows

    /// <summary>Opens (or focuses) the full player.</summary>
    private void ShowMainWindow()
    {
        if (_vm == null || _exiting) return;
        _trimTimer?.Stop();

        if (_window == null)
        {
            _window = new MainWindow(_vm);
            _window.Closed += (_, _) => OnMainWindowClosed();
            _window.Show();
            UpdateUiVisible();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Opens (or focuses) the miniplayer; the full window stays as it is.</summary>
    private void ShowMiniPlayer()
    {
        if (_vm == null || _exiting) return;
        _trimTimer?.Stop();

        if (_mini == null)
        {
            _mini = new MiniPlayerWindow(_vm);
            _mini.Closed += (_, _) => OnMiniPlayerClosed();
            _mini.Show();
            UpdateUiVisible();
        }
        _mini.Activate();
    }

    private void OnMainWindowClosed()
    {
        _window = null;
        if (_exiting) return;
        // The full window held all the covers; drop them even if the miniplayer stays open.
        AsyncImage.ClearCache();
        AfterWindowClosed();
    }

    private void OnMiniPlayerClosed()
    {
        _mini = null;
        if (_exiting) return;
        AfterWindowClosed();
    }

    private void AfterWindowClosed()
    {
        if (_switchingTheme) return; // the window is about to be reopened
        UpdateUiVisible();
        if (_window == null && _mini == null && _vm!.ConsumeTrayHint())
            _tray?.ShowBalloon("Yesify is still playing", "Click the tray icon to open it again. Right-click it to exit.");

        // Trim once the window is really gone, and again after finalizers have released images and handles.
        Dispatcher.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);
        _trimTimer?.Stop();
        _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer?.Stop();
            TrimMemory();
        };
        _trimTimer.Start();
    }

    /// <summary>
    /// Most of what Yesify touches is needed only once (starting up, loading a view) and would otherwise
    /// stay counted against it. Every 15 s: with no window open, trim once a minute (song changes and
    /// downloads touch new memory); in lightweight mode, also trim with the window open once it's been
    /// left alone for 10 s. Anything needed again comes back from RAM cheaply. Glass mode is left alone
    /// while a window is open, since its animations would just touch the pages again.
    /// </summary>
    private void StartMaintenance()
    {
        InputManager.Current.PreProcessInput += (_, _) =>
        {
            _lastInputTicks = Environment.TickCount64;
            _inputSinceTrim = true;
        };
        _maintenance = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => Maintain(), Dispatcher);
        _maintenance.Start();
    }

    private void Maintain()
    {
        if (_vm == null) return;
        var now = Environment.TickCount64;
        if (_window == null && _mini == null)
        {
            if (now - _lastTrimTicks >= 60_000)
            {
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
                _lastTrimTicks = now;
            }
        }
        else if (_vm.IsLightweight && _inputSinceTrim && now - _lastInputTicks >= 10_000)
        {
            TrimMemory();
            _lastTrimTicks = now;
            _inputSinceTrim = false;
        }
    }

    /// <summary>UI refresh only runs while some window shows it.</summary>
    private void UpdateUiVisible() => _vm?.SetUiVisible(_window != null || _mini != null);

    private void UpdateTooltip()
    {
        var t = _vm?.CurrentTrack;
        _tray?.SetTooltip(t == null ? "Yesify" : $"Yesify · {t.Title} - {t.Artist}");
    }

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Lightweight: flat dark panels, drawn by the CPU so the graphics driver never loads (least memory).
    /// Glass: translucent panels over the artwork background, drawn by the GPU.
    /// </summary>
    private void ApplyTheme(bool lightweight)
    {
        void Set(string key, string color) =>
            Resources[key] = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));

        if (lightweight)
        {
            Set("AccentBrush", "#FFFFFFFF");
            Set("AccentHoverBrush", "#E6FFFFFF");
            Set("SubtleTextBrush", "#FF9A9AA6");
            Set("FaintTextBrush", "#FF7A7A86");
            Set("InactiveIconBrush", "#FF9A9AA6");
            Set("LogoBrush", "#FFFFFFFF");
            Set("LogoForegroundBrush", "#FF0A0A0C");
            Set("ToastBrush", "#F02C2C33");
            Set("SettingsCardBrush", "#FF1A1A20");
            Set("PlayButtonBrush", "#FFF2F2F5");
            Set("PlayButtonBorderBrush", "#00000000");
            Set("PlayGlyphBrush", "#FF0A0A0C");
            Set("MenuBrush", "#FF1E1E24");
            Set("ScrollThumbBrush", "#FF3A3A44");
            Set("ScrollThumbHoverBrush", "#FF5A5A66");
            Set("ScrollThumbBorderBrush", "#00000000");
            Set("SwitchKnobOnBrush", "#FF101016");
            Resources["LikedTileBrush"] = Frozen(new LinearGradientBrush(Color.FromRgb(0x4A, 0x4A, 0x55), Color.FromRgb(0x26, 0x26, 0x2C), 45));
            Set("PanelBrush", "#FF141418");
            Set("RaisedBrush", "#FF1E1E24");
            Set("HoverBrush", "#FF26262E");
            Set("SelectedBrush", "#FF2C2C33");
            Set("DividerBrush", "#FF24242B");
            Set("PanelBorderBrush", "#00000000");
            Resources["MainPanelBrush"] = Frozen(new LinearGradientBrush(
                [new GradientStop(Color.FromRgb(0x26, 0x26, 0x2C), 0), new GradientStop(Color.FromRgb(0x14, 0x14, 0x18), 0.45)], 90));
        }
        else
        {
            // No purple in glass: accents are white, secondary text is translucent white.
            Set("AccentBrush", "#FFFFFFFF");
            Set("AccentHoverBrush", "#E6FFFFFF");
            Set("SubtleTextBrush", "#B3FFFFFF");
            Set("FaintTextBrush", "#80FFFFFF");
            Set("InactiveIconBrush", "#73FFFFFF");
            Set("LogoBrush", "#40FFFFFF");
            Set("LogoForegroundBrush", "#FFFFFFFF");
            Set("ToastBrush", "#99101016");
            Set("SettingsCardBrush", "#80141418"); // tint over the frost layer
            // Apple Music-style frosted play button instead of a solid white disc.
            Set("PlayButtonBrush", "#2EFFFFFF");
            Set("PlayButtonBorderBrush", "#4DFFFFFF");
            Set("PlayGlyphBrush", "#FFFFFFFF");
            // Frosted scrollbar pill.
            Set("MenuBrush", "#8C16161C"); // tint over the frost layer
            Set("ScrollThumbBrush", "#2EFFFFFF");
            Set("ScrollThumbHoverBrush", "#59FFFFFF");
            Set("ScrollThumbBorderBrush", "#33FFFFFF");
            Set("SwitchKnobOnBrush", "#FF101016");
            Resources["LikedTileBrush"] = Frozen(new LinearGradientBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 45));
            Set("PanelBrush", "#59101016");
            Set("RaisedBrush", "#1FFFFFFF");
            Set("HoverBrush", "#2EFFFFFF");
            Set("SelectedBrush", "#3DFFFFFF");
            Set("DividerBrush", "#1FFFFFFF");
            Set("PanelBorderBrush", "#24FFFFFF");
            Set("MainPanelBrush", "#38101016");
        }

        Frost.IsGlass = !lightweight;

        var mode = lightweight ? RenderMode.SoftwareOnly : RenderMode.Default;
        RenderOptions.ProcessRenderMode = mode;
        foreach (var w in new Window?[] { _window, _mini })
            if (w != null && PresentationSource.FromVisual(w) is HwndSource source)
                source.CompositionTarget.RenderMode = mode;
    }

    /// <summary>
    /// Applies the new look and reopens the open windows, so everything (drawing mode, colors picked by
    /// converters) comes back in the new theme. Playback state lives in the view model and is untouched.
    /// </summary>
    private void SwitchTheme()
    {
        ApplyTheme(_vm!.IsLightweight);
        Dispatcher.BeginInvoke(() =>
        {
            var reopenMain = _window != null;
            var reopenMini = _mini != null;
            var mainState = _window?.WindowState;
            _switchingTheme = true;
            _window?.Close();
            _mini?.Close();
            _switchingTheme = false;
            if (reopenMain)
            {
                ShowMainWindow();
                if (mainState == WindowState.Maximized) _window!.WindowState = WindowState.Maximized;
            }
            if (reopenMini) ShowMiniPlayer();
        });
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- exit

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _mini?.Close();
        _window?.Close();
        _media?.Dispose();
        _discord?.Dispose();
        _vm?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Returns freed memory to Windows: compact the GC heaps, then trim the working set.</summary>
    private static void TrimMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
