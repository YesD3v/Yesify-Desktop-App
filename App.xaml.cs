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
    private long _lastTrimTicks;
    private bool _exiting;

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

        // `--updated <pid>`: started by the updater, which is still saving and quitting. Wait for it to be
        // gone (it holds the single-instance lock), then clear away the files it left behind.
        var args = e.Args.ToList();
        Updater.Simulate = args.Remove("--simulate-update");
        var updatedFrom = args.IndexOf("--updated");
        if (updatedFrom >= 0)
        {
            if (updatedFrom + 1 < args.Count && int.TryParse(args[updatedFrom + 1], out var pid))
            {
                try { using var old = System.Diagnostics.Process.GetProcessById(pid); old.WaitForExit(20_000); }
                catch { /* already gone */ }
                args.RemoveAt(updatedFrom + 1);
            }
            args.RemoveAt(updatedFrom);
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
        Theme.Apply(Resources);
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
        };
        SessionEnding += (_, _) => ExitApp();

        // `--tray` starts without a window (e.g. for autostart); the rest is a search or link to open.
        var trayOnly = args.Remove("--tray");
        if (!trayOnly) ShowMainWindow();
        else AfterWindowClosed(); // same background mode as after closing the window
        StartMaintenance();

        // A newer Yesify on GitHub? Checked once per launch; the update screen offers it.
        _vm.RestartForUpdateRequested += ExitApp;
        Task.Run(Updater.CleanUpAfterUpdate);
        _ = _vm.CheckForUpdatesAsync();

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
    /// stay counted against it. Every 15 s with no window open, trim once a minute (song changes and
    /// downloads touch new memory). Anything needed again comes back from RAM cheaply. Memory is left alone
    /// while a window is open, since its animations would just touch the pages again.
    /// </summary>
    private void StartMaintenance()
    {
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
    }

    /// <summary>UI refresh only runs while some window shows it.</summary>
    private void UpdateUiVisible() => _vm?.SetUiVisible(_window != null || _mini != null);

    private void UpdateTooltip()
    {
        var t = _vm?.CurrentTrack;
        _tray?.SetTooltip(t == null ? "Yesify" : $"Yesify · {t.Title} - {t.Artist}");
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
    internal static void TrimMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    /// <summary>Gives back pages the app isn't touching (no garbage collection): cheap enough to repeat.</summary>
    internal static void TrimWorkingSet() => SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
