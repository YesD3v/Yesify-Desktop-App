using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Yesify.Models;
using Yesify.Services;
using Yesify.ViewModels;

namespace Yesify;

/// <summary>
/// The UI only. It is created when shown and thrown away when closed (Yesify keeps playing from the tray),
/// so it must not hold on to anything the view model outlives.
/// </summary>
public partial class MainWindow : Window, IFrostSource
{
    /// <summary>Behind a pop-up: the artwork backdrop, then the panels.</summary>
    public IEnumerable<Visual> FrostLayers => [Backdrop, Root];

    private readonly MainViewModel _vm;
    private readonly LyricsAnimator _lyrics;
    private readonly LyricsAnimator _cinemaLyrics;
    private string? _backdropUrl;
    private bool _backdropOnA;

    private MainViewModel Vm => _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        // The header is the song list's first row, so it scrolls away with the songs (the list stays virtualized).
        TrackList.ItemsSource = new CompositeCollection
        {
            new HeaderMarker(),
            new TopSectionsMarker(),
            new CollectionContainer { Collection = vm.Tracks },
            new SearchMoreMarker(), // hidden outside Search
            new CollectionContainer { Collection = vm.SearchOnline },
            new SectionsMarker(),
        };

        _lyrics = new LyricsAnimator(vm, LyricsList, LyricsScroller);
        LyricsList.IsVisibleChanged += (_, _) => _lyrics.Refresh();
        _cinemaLyrics = new LyricsAnimator(vm, CinemaLyricsList, CinemaLyricsScroller, anchor: 0.4);
        CinemaLyricsList.IsVisibleChanged += (_, _) => _cinemaLyrics.Refresh();

        StateChanged += (_, _) => { UpdateMaximizedState(); RepaintAll(); UpdateBackdropMotion(); };
        Activated += (_, _) => RepaintAll();
        IsVisibleChanged += (_, _) => { RepaintAll(); UpdateBackdropMotion(); };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSystemWake;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnSystemWake;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemWake;
        Vm.PropertyChanged += OnVmPropertyChanged;
        Vm.HomeSections.CollectionChanged += OnHomeSectionsChanged;
        Vm.Tracks.CollectionChanged += OnSongCountChanged;
        Vm.SearchOnline.CollectionChanged += OnSongCountChanged;
        // Library: a header, rows of tiles cut to the page width, then matching songs (virtualized).
        LibraryList.ItemsSource = new CompositeCollection
        {
            new LibraryHeaderMarker(),
            new CollectionContainer { Collection = _libraryRows },
            new LibrarySongsMarker(),
        };
        Vm.LibraryItems.CollectionChanged += OnLibraryItemsChanged;
        LibraryList.SizeChanged += (_, _) => RebuildLibraryRows(force: false);
        Vm.BackdropSettingsChanged += ApplyBackdropSettings;
        ApplyBackdropSettings();
        SetUpTaskbarButtons();
        UpdateVirtualization();
        UpdateRightPanel();
        UpdateSidebarWidth();
        _ = UpdateBackdropAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11 rounded corners (with the system shadow). Windows squares them off by itself when
        // the window is maximized, so they only show while it's windowed.
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // After a long time in the background (or a fullscreen game taking the GPU), WPF can come back
    // showing a stale frame and only repaint the bits that change, like a hovered button. Asking for a
    // full repaint whenever the window comes back draws everything again.
    // Unlocking, waking from sleep and screen changes, which can happen without the window being re-activated.
    private void OnSystemWake(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RepaintAll);

    private void RepaintAll()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        Dispatcher.BeginInvoke(() =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0080 | 0x0100); // RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    protected override void OnClosed(EventArgs e)
    {
        // The view model lives on; unhook so it doesn't keep this window (and all its images) alive.
        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.HomeSections.CollectionChanged -= OnHomeSectionsChanged;
        Vm.Tracks.CollectionChanged -= OnSongCountChanged;
        Vm.SearchOnline.CollectionChanged -= OnSongCountChanged;
        Vm.LibraryItems.CollectionChanged -= OnLibraryItemsChanged;
        Vm.BackdropSettingsChanged -= ApplyBackdropSettings;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSystemWake;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnSystemWake;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemWake;
        _lyrics.Stop();
        _cinemaLyrics.Stop();
        DataContext = null;
        base.OnClosed(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentLyricIndex):
            case nameof(MainViewModel.IsPlaying):
            case nameof(MainViewModel.ShowLyrics):
                _lyrics.Refresh();
                _cinemaLyrics.Refresh();
                if (e.PropertyName == nameof(MainViewModel.ShowLyrics) && Vm.ShowLyrics) EaseInPane();
                if (e.PropertyName == nameof(MainViewModel.IsPlaying)) UpdateTaskbarButtons();
                break;
            case nameof(MainViewModel.IsCinema):
                UpdateCinema();
                break;
            case nameof(MainViewModel.IsMuted):
                UpdateTaskbarButtons();
                break;
            case nameof(MainViewModel.LibraryListMode):
                RebuildLibraryRows(force: true);
                break;
            case nameof(MainViewModel.IsRightPanelOpen):
                UpdateRightPanel();
                break;
            case nameof(MainViewModel.ShowQueue):
                ScrollQueueToNowPlaying();
                if (Vm.ShowQueue) EaseInPane();
                break;
            case nameof(MainViewModel.CurrentTrack):
                ScrollQueueToNowPlaying();
                break;
            case nameof(MainViewModel.BackdropImageUrl):
                _ = UpdateBackdropAsync();
                break;
            case nameof(MainViewModel.SidebarCollapsed):
                UpdateSidebarWidth();
                break;
            case nameof(MainViewModel.ShowPageSkeleton):
                UpdateSkeleton();
                break;
            case nameof(MainViewModel.CurrentView):
            case nameof(MainViewModel.ViewTitle):
                // A page behind the skeleton makes its entrance when it's revealed instead.
                if (!Vm.ShowPageSkeleton && !_revealing) EaseInView();
                break;
        }
    }

    private readonly System.Diagnostics.Stopwatch _skeletonClock = new();
    private bool _revealing;

    /// <summary>
    /// The loading skeleton appears the moment a page is opened (it's always laid out, just hidden). When the
    /// page is ready it's first laid out invisibly behind the skeleton; only then do the two cross-fade, so
    /// building a heavy page (rows of tiles, pictures) never eats the first frames of the animation.
    /// </summary>
    private void UpdateSkeleton()
    {
        if (Vm.ShowPageSkeleton)
        {
            _revealing = false;
            MainContent.BeginAnimation(OpacityProperty, null);
            if (MainContent.RenderTransform is TranslateTransform { IsFrozen: false } stay) stay.BeginAnimation(TranslateTransform.YProperty, null);
            PageSkeleton.Visibility = Visibility.Visible;
            // Starts faint and settles in fast: instant to the eye, and a page that turns out to be ready a
            // frame later (cached) doesn't flash a full skeleton.
            PageSkeleton.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _skeletonClock.Restart();
            return;
        }
        if (PageSkeleton.Visibility != Visibility.Visible) return;

        // Every page (even one that's ready at once, like Liked Songs) is built invisibly behind the
        // skeleton and then cross-faded in, so building it never lands in the middle of an animation.
        _revealing = true;
        TrackList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero)); // invisible while it's built
        Dispatcher.BeginInvoke(async () =>
        {
            // Loaded priority runs after layout and the next render: the page is built by now.
            // The shimmer stays up a moment at least, so a fast page doesn't flash it.
            var wait = 240 - (int)_skeletonClock.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait);
            _revealing = false;
            if (Vm.ShowPageSkeleton) return; // another page started loading meanwhile
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // While it fades and rises, the page is drawn once into a bitmap and that is moved, instead of
            // every tile and cover being redrawn each frame.
            TrackList.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX };
            var show = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
            TrackList.BeginAnimation(OpacityProperty, show);
            if (TrackList.RenderTransform is not TranslateTransform { IsFrozen: false } rise)
                TrackList.RenderTransform = rise = new TranslateTransform();
            var lift = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
            lift.Completed += (_, _) => TrackList.CacheMode = null; // back to live drawing (sharp text, scrolling)
            rise.BeginAnimation(TranslateTransform.YProperty, lift);
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (!Vm.ShowPageSkeleton) HideSkeleton(); };
            PageSkeleton.BeginAnimation(OpacityProperty, fade);
        }, DispatcherPriority.Loaded);
    }

    private void HideSkeleton()
    {
        PageSkeleton.BeginAnimation(OpacityProperty, null);
        PageSkeleton.Opacity = 0;
        PageSkeleton.Visibility = Visibility.Hidden; // stays laid out for next time
        TrackList.BeginAnimation(OpacityProperty, null);
        if (TrackList.RenderTransform is TranslateTransform { IsFrozen: false, HasAnimatedProperties: false }) TrackList.CacheMode = null;
    }

    /// <summary>A new page (Home, Library, a playlist) fades and rises into place instead of snapping.</summary>
    private void EaseInView()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MainContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(200)));
        if (MainContent.RenderTransform is TranslateTransform { IsFrozen: false } move)
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private bool? _virtualizing;

    private void OnSongCountChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateVirtualization();

    /// <summary>
    /// A short page (an artist, an album, a playlist of a few dozen songs) is laid out whole. Virtualized,
    /// its tall rows (the header with the artwork, rows of album tiles) were torn down when scrolled out of
    /// view and rebuilt, re-measured and their pictures reloaded when scrolled back, which made scrolling
    /// up stutter and jump. Long lists stay virtualized so a 1,000-song playlist doesn't build every row.
    /// </summary>
    private void UpdateVirtualization()
    {
        var virtualize = Vm.Tracks.Count + Vm.SearchOnline.Count > 40;
        if (virtualize == _virtualizing) return;
        _virtualizing = virtualize;
        VirtualizingPanel.SetIsVirtualizing(TrackList, virtualize);
    }

    /// <summary>Fresh picks on Home fade up into place instead of popping in.</summary>
    private void OnHomeSectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset || !Vm.IsHomeView || !IsVisible) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HomeSectionsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        if (HomeSectionsList.RenderTransform is not TranslateTransform { IsFrozen: false } move)
            HomeSectionsList.RenderTransform = move = new TranslateTransform();
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
    }

    // ---------------------------------------------------------------- glass backdrop

    /// <summary>Cross-fades to the new artwork and tints the window with its colors.</summary>
    private async Task UpdateBackdropAsync()
    {
        var url = Vm.BackdropImageUrl;
        if (url == _backdropUrl) return;
        _backdropUrl = url;

        var backdrop = await CoverColors.GetBackdropAsync(url);
        if (url != _backdropUrl) return; // a newer change won

        var duration = new Duration(TimeSpan.FromMilliseconds(800));
        var (show, hide) = _backdropOnA ? (BackB, BackA) : (BackA, BackB);
        _backdropOnA = !_backdropOnA;
        show.Source = backdrop.Image;
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
        hide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));

        _coverTint = (backdrop.A, backdrop.B, backdrop.C);
        if (Vm.BackdropMode != "color") AnimateTint(backdrop.A, backdrop.B, backdrop.C, duration);
    }

    private (Color A, Color B, Color C) _coverTint = (CoverColors.Fallback.A, CoverColors.Fallback.B, CoverColors.Fallback.B);

    private void AnimateTint(Color a, Color b, Color c, Duration duration)
    {
        TintA.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(a, duration));
        TintB.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(b, duration));
        TintC.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(c, duration));
    }

    /// <summary>
    /// Settings, Background: the cover or custom colors, tint strength, brightness, motion. Called at
    /// start and whenever one changes; changes ease in.
    /// </summary>
    private void ApplyBackdropSettings()
    {
        var d = new Duration(TimeSpan.FromMilliseconds(450));
        var custom = Vm.BackdropMode == "color";
        BackImages.BeginAnimation(OpacityProperty, new DoubleAnimation(custom ? 0 : 1, d));
        TintLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(custom ? 1 : Vm.BackdropTint / 100, d));
        var bright = Vm.BackdropBrightness;
        DimLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(Math.Clamp((100 - bright) / 50, 0, 1), d));
        DarkLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(bright < 50 ? (50 - bright) / 50 * 0.65 : 0, d));
        if (custom)
        {
            var a = Vm.BackdropColorA;
            var b = Vm.BackdropColorB;
            var mid = Color.FromRgb((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));
            AnimateTint(a, b, mid, d);
        }
        else AnimateTint(_coverTint.A, _coverTint.B, _coverTint.C, d);
        UpdateBackdropMotion();
    }

    private bool _motionOn;
    private double _motionFps;

    /// <summary>
    /// Motion: the blurred cover slowly turns and drifts (it's a cached bitmap, so moving it is cheap for the
    /// GPU), and the tint's direction swings. Runs at 30 fps; stopped while the window is minimized or hidden.
    /// </summary>
    private void UpdateBackdropMotion()
    {
        var on = Vm.BackdropMotion && IsVisible && WindowState != WindowState.Minimized;
        var fps = Vm.BackdropMotionFps;
        if (on == _motionOn && (!on || fps == _motionFps)) return;
        _motionOn = on;
        _motionFps = fps;
        if (BackImages.RenderTransform is not TransformGroup { Children: [ScaleTransform scale, RotateTransform rotate, TranslateTransform move] })
            return;
        if (!on)
        {
            var settle = new Duration(TimeSpan.FromMilliseconds(900));
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, settle) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, settle) { EasingFunction = ease });
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, settle) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, settle) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, settle) { EasingFunction = ease });
            TintBrush.BeginAnimation(LinearGradientBrush.StartPointProperty, null);
            TintBrush.BeginAnimation(LinearGradientBrush.EndPointProperty, null);
            return;
        }
        DoubleAnimation Loop(double from, double to, double seconds)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(a, (int)fps);
            return a;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1.22, 1.34, 17));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1.22, 1.34, 17));
        rotate.BeginAnimation(RotateTransform.AngleProperty, Loop(-9, 9, 23));
        move.BeginAnimation(TranslateTransform.XProperty, Loop(-70, 70, 29));
        move.BeginAnimation(TranslateTransform.YProperty, Loop(45, -45, 19));
        PointAnimation Swing(Point from, Point to)
        {
            var a = new PointAnimation(from, to, TimeSpan.FromSeconds(21)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(a, (int)fps);
            return a;
        }
        TintBrush.BeginAnimation(LinearGradientBrush.StartPointProperty, Swing(new Point(0, 0), new Point(1, 0)));
        TintBrush.BeginAnimation(LinearGradientBrush.EndPointProperty, Swing(new Point(1, 1), new Point(0, 1)));
    }

    // ---------------------------------------------------------------- right panel

    public static readonly DependencyProperty RightPanelOpenWidthProperty = DependencyProperty.Register(
        nameof(RightPanelOpenWidth), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).RightColumn.Width = new GridLength((double)e.NewValue)));

    /// <summary>The right panel's column width while it slides (animated; copied into the column).</summary>
    public double RightPanelOpenWidth
    {
        get => (double)GetValue(RightPanelOpenWidthProperty);
        set => SetValue(RightPanelOpenWidthProperty, value);
    }

    private bool _rightSized;
    private bool? _rightOpen;

    /// <summary>
    /// Opens or closes the lyrics / queue panel. It slides in from the right and fades in (or slides out and
    /// fades away). Like the sidebar, the page keeps its final width while the edge moves, and the panel keeps
    /// its own width, so neither is re-laid out on every frame.
    /// </summary>
    private void UpdateRightPanel()
    {
        var open = Vm.IsRightPanelOpen;
        if (open == _rightOpen) return;
        _rightOpen = open;
        var width = Math.Clamp(Vm.RightPanelWidth, 260, 640);
        BeginAnimation(RightPanelOpenWidthProperty, null);
        if (!_rightSized || !IsLoaded)
        {
            // first layout: no animation
            _rightSized = true;
            RightPanel.Visibility = RightSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            RightColumn.MinWidth = open ? 260 : 0;
            RightPanelOpenWidth = open ? width : 0;
            RightPanel.ClearValue(WidthProperty);
            RightPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var d = TimeSpan.FromMilliseconds(open ? 320 : 260);
        var from = RightColumn.ActualWidth;
        var to = open ? width : 0;
        RightColumn.MinWidth = 0;
        RightPanel.Width = width;
        RightPanel.HorizontalAlignment = HorizontalAlignment.Left;
        RightPanel.Visibility = Visibility.Visible;
        RightSplitter.Visibility = Visibility.Visible;
        if (MainPanel.ActualWidth > 0)
        {
            MainContent.Width = Math.Max(0, MainPanel.ActualWidth - MainPanel.BorderThickness.Left - MainPanel.BorderThickness.Right - (to - from));
            MainContent.HorizontalAlignment = HorizontalAlignment.Left;
        }
        if (RightPanel.RenderTransform is not TranslateTransform { IsFrozen: false } slide)
            RightPanel.RenderTransform = slide = new TranslateTransform();
        if (open)
        {
            // Hidden until it's built (the queue can take a moment the first time), then the whole slide plays.
            RightPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(36, TimeSpan.Zero));
        }

        var grow = new DoubleAnimation(from, to, d) { EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            if (Vm.IsRightPanelOpen != open) return; // toggled again meanwhile
            BeginAnimation(RightPanelOpenWidthProperty, null);
            RightPanelOpenWidth = to;
            RightColumn.MinWidth = open ? 260 : 0;
            RightPanel.ClearValue(WidthProperty);
            RightPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (!open) RightPanel.Visibility = RightSplitter.Visibility = Visibility.Collapsed;
            MainContent.ClearValue(WidthProperty);
            MainContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        };
        void Run()
        {
            if (Vm.IsRightPanelOpen != open) return;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(open ? 36 : 0, open ? 0 : 36, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            RightPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, d));
            BeginAnimation(RightPanelOpenWidthProperty, grow);
        }
        // Loaded priority runs after the next layout and render: the panel is built by then.
        if (open) Dispatcher.BeginInvoke(Run, DispatcherPriority.Loaded);
        else Run();
    }

    /// <summary>Switching between Lyrics and Queue: the pane that appears fades and rises into place.</summary>
    private void EaseInPane()
    {
        if (!Vm.IsRightPanelOpen || RightPanel.Visibility != Visibility.Visible) return;
        FrameworkElement pane = Vm.ShowLyrics ? LyricsPane : QueueScroller;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (pane.RenderTransform is not TranslateTransform { IsFrozen: false } rise)
            pane.RenderTransform = rise = new TranslateTransform();
        pane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
        // after it's built, so the fade isn't spent while the queue lays itself out
        Dispatcher.BeginInvoke(() =>
        {
            pane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
            rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        }, DispatcherPriority.Loaded);
    }

    private void RightSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        Vm.RightPanelWidth = RightColumn.ActualWidth;

    // ---------------------------------------------------------------- keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.Escape && Vm.IsCinema && !Vm.IsSettingsOpen && !Vm.IsEditingPlaylist && !Vm.IsImporting)
        {
            if (_fullscreen) SetFullscreen(false);
            else Vm.IsCinema = false;
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && Vm.IsCinema)
        {
            SetFullscreen(!_fullscreen);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (Vm.IsSettingsOpen || Vm.IsEditingPlaylist || Vm.IsImporting))
        {
            Vm.CloseSettingsCommand.Execute(null);
            Vm.CancelEditPlaylistCommand.Execute(null);
            Vm.CancelImportCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && !typing)
        {
            Vm.TogglePlayCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control && !typing)
        {
            Vm.NextCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control && !typing)
        {
            Vm.PreviousCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !typing && TrackList.SelectedItem is Track t)
        {
            Vm.PlayFromCommand.Execute(t);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---------------------------------------------------------------- import tile

    // ---------------------------------------------------------------- text boxes in dialogs

    /// <summary>Puts the cursor in a text box as soon as it appears (search, dialogs).</summary>
    private void FocusWhenShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(() => { Keyboard.Focus(box); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Search box: cursor in, at the end of what's there (never select, or the next key replaces it).</summary>
    private void SearchBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            Dispatcher.BeginInvoke(() => { Keyboard.Focus(box); box.CaretIndex = box.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EditorBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => Vm.CancelEditPlaylistCommand.Execute(null);

    private void ImportBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => Vm.CancelImportCommand.Execute(null);

    private void EditorCover_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a cover image",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) Vm.SetPickedCover(dialog.FileName);
    }

    // ---------------------------------------------------------------- track list

    private void TrackRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Track t }) Vm.PlayFromCommand.Execute(t);
    }

    // ---------------------------------------------------------------- menus

    private static MenuItem Item(string header, Action onClick, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>"Add to Playlist ▸": New Playlist…, then your playlists and imported ones (except the one you're in).</summary>
    private MenuItem AddToPlaylistMenu(string header, Func<List<Track>> tracks) =>
        AddToPlaylistMenu(header, () => Task.FromResult<List<Track>?>(tracks()));

    /// <summary>The same, for songs that may have to be fetched first (an album or artist on Home, an imported playlist).</summary>
    private MenuItem AddToPlaylistMenu(string header, Func<Task<List<Track>?>> tracks)
    {
        // Loads the songs when an entry is clicked; nothing happens if they can't be loaded (the view model says why).
        MenuItem Entry(string text, Action<List<Track>> act) => Item(text, async () =>
        {
            if (await tracks() is { Count: > 0 } songs) act(songs);
        });

        var menu = new MenuItem { Header = header };
        menu.Items.Add(Entry("New Playlist…", songs => Vm.BeginNewPlaylist(songs)));
        var targets = Vm.UserPlaylists.Where(p => p != Vm.CurrentUserPlaylist).ToList();
        var imported = Vm.ImportedPlaylists.Where(p => p.Id != Vm.CurrentCollection?.Id).ToList();
        if (targets.Count + imported.Count > 0) menu.Items.Add(new Separator());
        foreach (var p in targets) menu.Items.Add(Entry(p.Name, songs => Vm.AddToPlaylist(p, songs)));
        foreach (var p in imported) menu.Items.Add(Entry(p.Name, songs => _ = Vm.AddToImportedAsync(p, songs)));
        return menu;
    }

    /// <summary>
    /// What every song's menu has, wherever the song is shown (a list, Home, the queue, the player bar):
    /// Play Next, Add to Queue, like, Add to Playlist.
    /// </summary>
    private void AddSongItems(ItemCollection items, Track track)
    {
        items.Add(Item("Play Next", () => Vm.PlayNextCommand.Execute(track)));
        items.Add(Item("Add to Queue", () => Vm.AddToQueueCommand.Execute(track)));
        items.Add(new Separator());
        items.Add(Item(Vm.IsLiked(track) ? "Remove from Liked Songs" : "Add to Liked Songs", () => Vm.ToggleLikeCommand.Execute(track)));
        items.Add(AddToPlaylistMenu("Add to Playlist", () => [track]));
        items.Add(new Separator());
        items.Add(Item("Start Radio", () => Vm.StartRadioCommand.Execute(track)));
        items.Add(Item("Go to Artist", () => Vm.GoToArtistCommand.Execute(track), track.Artist.Length > 0));
        items.Add(Item("Go to Album", () => Vm.GoToAlbumCommand.Execute(track), MainViewModel.HasAlbum(track)));
    }

    // Artist and album names in song rows and the player bar open their pages.
    private Track? LinkTrack(object sender) =>
        (sender as FrameworkElement)?.DataContext as Track ?? Vm.CurrentTrack;

    private void ArtistLink_Click(object sender, MouseButtonEventArgs e)
    {
        if (LinkTrack(sender) is not { } t || t.Artist.Length == 0) return;
        e.Handled = true;
        Vm.GoToArtistCommand.Execute(t);
    }

    private void AlbumLink_Click(object sender, MouseButtonEventArgs e)
    {
        if (LinkTrack(sender) is not { } t || !MainViewModel.HasAlbum(t)) return;
        e.Handled = true;
        Vm.GoToAlbumCommand.Execute(t);
    }

    // The mouse's back button goes back, like a browser.
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && Vm.CanGoBack)
        {
            Vm.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        base.OnMouseUp(e);
    }

    /// <summary>
    /// What every collection's menu has (a playlist, album, mix, artist, Liked Songs), opened or not:
    /// Play, Shuffle Play, Play Next, Add to Queue (in order or shuffled), Add to Playlist.
    /// </summary>
    private void AddCollectionItems(ItemCollection items, string name, Func<Task<List<Track>?>> tracks)
    {
        MenuItem Entry(string text, Action<List<Track>> act) => Item(text, async () =>
        {
            if (await tracks() is { Count: > 0 } songs) act(songs);
        });

        items.Add(Entry("Play", songs => Vm.PlayTracks(songs, shuffled: false)));
        items.Add(Entry("Shuffle Play", songs => Vm.PlayTracks(songs, shuffled: true)));
        items.Add(new Separator());
        items.Add(Entry("Play Next", songs => Vm.QueueAll(songs, shuffled: false, name, next: true)));
        items.Add(Entry("Add to Queue", songs => Vm.QueueAll(songs, shuffled: false, name)));
        items.Add(Entry("Add to Queue, Shuffled", songs => Vm.QueueAll(songs, shuffled: true, name)));
        items.Add(new Separator());
        items.Add(AddToPlaylistMenu("Add to Playlist", tracks));
    }

    // Right-clicking a song selects it; the menu acts on the selection.
    private void TrackList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (TrackList.SelectedItem is not Track track || TrackList.ContextMenu is not { } menu)
        {
            e.Handled = true; // the header row: no menu
            return;
        }
        menu.Items.Clear();
        menu.Items.Add(Item("Play", () => Vm.PlayFromCommand.Execute(track)));
        AddSongItems(menu.Items, track);
        if (Vm.CurrentUserPlaylist != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove from this Playlist", () => Vm.RemoveFromCurrentPlaylist(track)));
        }
    }

    /// <summary>Home tiles: a song gets the song menu; a mix, album or artist gets the collection menu.</summary>
    private void HomeTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Services.HomeItem item, ContextMenu: { } menu })
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        if (item.Kind == HomeItemKind.Track)
        {
            if (item.Tracks == null || item.Index >= item.Tracks.Count)
            {
                e.Handled = true;
                return;
            }
            menu.Items.Add(Item("Play", () => Vm.OpenHomeItemCommand.Execute(item)));
            AddSongItems(menu.Items, item.Tracks[item.Index]);
            return;
        }
        menu.Items.Add(Item("Open", () => Vm.OpenHomeItemCommand.Execute(item)));
        if (item.Kind == HomeItemKind.MusicCategory) return;
        menu.Items.Add(new Separator());
        // Fetched at most once per menu, however many entries are clicked.
        Task<List<Track>?>? loading = null;
        AddCollectionItems(menu.Items, item.Title, () => loading ??= Vm.HomeItemTracksAsync(item));
        if (item.Kind == HomeItemKind.MusicArtist && item.Id is { } artistId)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Start Artist Mix", () => _ = Vm.StartArtistRadioAsync(artistId, item.Title)));
        }
        if (item.Kind is HomeItemKind.MusicAlbum or HomeItemKind.MusicPlaylist)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Vm.IsSaved(item) ? "In Your Library" : "Add to Library", () => Vm.SaveMusicItem(item), !Vm.IsSaved(item)));
        }
    }

    /// <summary>Queue panel rows (Up next and Recently played).</summary>
    private void QueueRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QueueItem item, ContextMenu: { } menu })
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        menu.Items.Add(Item("Play", () => Vm.PlayQueueItemCommand.Execute(item)));
        AddSongItems(menu.Items, item.Track);
        if (item.Source == QueueSource.Queued)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove from Queue", () => Vm.RemoveQueueItemCommand.Execute(item)));
        }
    }

    /// <summary>The playing song, in the player bar and at the top of the queue.</summary>
    private void NowPlaying_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Vm.CurrentTrack is not { } track || sender is not FrameworkElement { ContextMenu: { } menu })
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        AddSongItems(menu.Items, track);
    }

    /// <summary>"…" on an album / playlist / Liked Songs: everything in it goes somewhere.</summary>
    private void HeaderMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var all = Vm.ShownTracks.ToList();
        menu.Items.Add(Item("Play Next", () => Vm.QueueView(shuffled: false, next: true), all.Count > 0));
        menu.Items.Add(Item("Add to Queue", () => Vm.QueueView(shuffled: false), all.Count > 0));
        menu.Items.Add(Item("Add to Queue, Shuffled", () => Vm.QueueView(shuffled: true), all.Count > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(AddToPlaylistMenu("Add All to Playlist", () => all));
        menu.Items.Add(Item("Copy to New Playlist…", Vm.CopyViewToNewPlaylist, all.Count > 0));
        if (Vm.ViewAllSongsId != null)
        {
            // An artist: everything they've released, not just the top songs on the page.
            Task<List<Track>?>? loading = null;
            menu.Items.Add(new Separator());
            AddCollectionItems(menu.Items, Vm.ViewTitle, () => loading ??= Vm.ViewAllSongsAsync());
            foreach (var mi in menu.Items.OfType<MenuItem>().Skip(menu.Items.OfType<MenuItem>().Count() - 6))
                if (mi.Header is string h) mi.Header = h switch
                {
                    "Play" => "Play All Their Songs",
                    "Shuffle Play" => "Shuffle All Their Songs",
                    "Play Next" => "Play All Their Songs Next",
                    "Add to Queue" => "Add All Their Songs to Queue",
                    "Add to Queue, Shuffled" => "Add All Their Songs to Queue, Shuffled",
                    "Add to Playlist" => "Add All Their Songs to Playlist",
                    _ => h,
                };
        }
        if (Vm.CurrentUserPlaylist is { } p)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Edit Details…", () => Vm.BeginEditPlaylist(p)));
            menu.Items.Add(Item("Delete Playlist", () => ConfirmDelete(p)));
        }
        else if (Vm.CurrentCollection is { } imported)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Edit…", () => _ = Vm.EditImportedAsync(imported)));
        }
        menu.IsOpen = true;
    }

    private void ConfirmDelete(UserPlaylist p)
    {
        var answer = MessageBox.Show(this, $"Delete “{p.Name}”? This can't be undone.", "Delete Playlist", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) Vm.DeletePlaylist(p);
    }

    // ---------------------------------------------------------------- library tiles

    private void HomeTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Services.HomeItem item }) Vm.OpenHomeItemCommand.Execute(item);
    }

    private void LibraryTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryItem item }) Vm.OpenLibraryItemCommand.Execute(item);
    }

    private void LibraryTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryItem item, ContextMenu: { } menu }) return;
        menu.Items.Clear();
        menu.Items.Add(Item(Vm.IsPinned(item) ? "Unpin" : "Pin to Top", () => Vm.TogglePin(item)));
        menu.Items.Add(new Separator());
        // Fetched at most once per menu, however many entries are clicked.
        Task<List<Track>?>? loading = null;
        void AddCollection()
        {
            menu.Items.Add(Item("Open", () => Vm.OpenLibraryItemCommand.Execute(item)));
            menu.Items.Add(new Separator());
            AddCollectionItems(menu.Items, item.Title, () => loading ??= Vm.LibraryItemTracksAsync(item));
        }
        switch (item.Kind)
        {
            case LibraryItemKind.Liked:
                AddCollection();
                break;
            case LibraryItemKind.UserPlaylist when Vm.UserPlaylists.FirstOrDefault(p => p.Id == item.Id) is { } p:
                AddCollection();
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Edit Details…", () => Vm.BeginEditPlaylist(p)));
                menu.Items.Add(Item("Delete Playlist", () => ConfirmDelete(p)));
                break;
            case LibraryItemKind.Saved when item.Saved != null:
                AddCollection();
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Edit…", () => _ = Vm.EditImportedAsync(item.Saved)));
                menu.Items.Add(Item("Remove from Library", () => Vm.RemoveSavedCommand.Execute(item.Saved)));
                break;
            case LibraryItemKind.Album:
                menu.Items.Add(Item("Open Album", () => Vm.OpenLibraryItemCommand.Execute(item)));
                break;
            default:
                e.Handled = true; // New / Import: nothing to manage
                break;
        }
    }

    // ---------------------------------------------------------------- sidebar

    // GridLength can't be animated directly, so a double property is animated and copied into the column.
    public static readonly DependencyProperty SidebarWidthProperty = DependencyProperty.Register(
        nameof(SidebarWidth), typeof(double), typeof(MainWindow),
        new PropertyMetadata(248.0, (d, e) => ((MainWindow)d).SidebarColumn.Width = new GridLength((double)e.NewValue)));

    public double SidebarWidth
    {
        get => (double)GetValue(SidebarWidthProperty);
        set => SetValue(SidebarWidthProperty, value);
    }

    private bool _sidebarSized;

    /// <summary>Slides the sidebar open or shut (instantly the first time, when the window opens).</summary>
    /// <summary>
    /// The sidebar slides open or shut. While it moves, the page keeps its final width (pinned to the
    /// panel's left edge, the panel clipping it) so it's laid out once instead of re-flowing every frame:
    /// only the panel's edge moves, which is what made the animation choppy before.
    /// </summary>
    private void UpdateSidebarWidth()
    {
        var target = Vm.SidebarCollapsed ? 72.0 : 248.0;
        if (!_sidebarSized)
        {
            _sidebarSized = true;
            SidebarWidth = target;
            SidebarColumn.Width = new GridLength(target);
            return;
        }
        if (MainPanel.ActualWidth > 0)
        {
            MainContent.Width = Math.Max(0, MainPanel.ActualWidth - MainPanel.BorderThickness.Left - MainPanel.BorderThickness.Right - (target - SidebarWidth));
            MainContent.HorizontalAlignment = HorizontalAlignment.Left;
        }
        var slide = new DoubleAnimation(target, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        slide.Completed += (_, _) =>
        {
            MainContent.ClearValue(WidthProperty);
            MainContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        };
        BeginAnimation(SidebarWidthProperty, slide);
    }

    // ---------------------------------------------------------------- library rows

    private readonly BulkObservableCollection<LibraryRowGroup> _libraryRows = new();
    private int _libraryPerRow = -1;

    private void OnLibraryItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RebuildLibraryRows(force: true);

    /// <summary>Cuts the Library's tiles into rows that fit the page (or one per row in list view).</summary>
    private void RebuildLibraryRows(bool force)
    {
        var width = LibraryList.ActualWidth - 24 - 20 - 14; // row margins and the scrollbar
        var per = Vm.LibraryListMode ? 1 : Math.Max(1, (int)(width / 188)); // a tile is 184 + 4
        if (!force && (per == _libraryPerRow || width <= 0)) return;
        _libraryPerRow = per;
        var items = Vm.LibraryItems;
        var rows = new List<LibraryRowGroup>((items.Count + per - 1) / per);
        for (var i = 0; i < items.Count; i += per)
            rows.Add(new LibraryRowGroup(items.Skip(i).Take(per).ToList(), Vm.LibraryListMode));
        _libraryRows.ReplaceAll(rows);
    }
    // ---------------------------------------------------------------- queue panel

    // Up next rows can be dragged to reorder: the row follows the pointer while the others slide out of
    // its way; a press without movement plays the song.

    private QueueItem? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private int _dragFrom = -1, _dragTarget = -1;
    private double _rowHeight;
    private readonly List<ContentPresenter> _dragRows = [];

    private static QueueItem? ItemAt(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: QueueItem item }) return item;
        return null;
    }

    private void HistoryList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(e.OriginalSource) is { } item) Vm.PlayQueueItemCommand.Execute(item);
    }

    /// <summary>Puts Now playing at the top of the queue, so Recently played sits above the fold (scroll up for it).</summary>
    private void ScrollQueueToNowPlaying()
    {
        if (!Vm.ShowQueue) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (NowPlayingBlock == null || QueueContent == null || QueueScroller == null) return;
            QueueContent.UpdateLayout();
            var top = NowPlayingBlock.TransformToAncestor(QueueContent).Transform(new Point(0, 0)).Y;
            QueueScroller.ScrollToVerticalOffset(top);
        }));
    }

    private void UpNext_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = ItemAt(e.OriginalSource);
        _pressPoint = e.GetPosition(UpNextList);
    }

    private void UpNext_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(UpNextList);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _pressPoint.Y) < 5) return;
            BeginDrag();
            if (!_dragging) return;
        }
        UpdateDrag(p);
        AutoScroll(e.GetPosition(QueueScroller).Y);
    }

    private void UpNext_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) EndDrag(commit: true);
        else if (_pressed != null && ItemAt(e.OriginalSource) == _pressed) Vm.PlayQueueItemCommand.Execute(_pressed);
        _pressed = null;
    }

    private void UpNext_LostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging) EndDrag(commit: true);
    }

    private void BeginDrag()
    {
        _dragFrom = Vm.UpNextView.IndexOf(_pressed!);
        if (_dragFrom < 0) return;
        _dragRows.Clear();
        for (var i = 0; i < Vm.UpNextView.Count; i++)
        {
            if (UpNextList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) return;
            cp.RenderTransform = new TranslateTransform();
            _dragRows.Add(cp);
        }
        _rowHeight = _dragRows[_dragFrom].ActualHeight;
        _dragTarget = _dragFrom;
        _dragging = true;

        // Lift the dragged row above the others.
        var row = _dragRows[_dragFrom];
        Panel.SetZIndex(row, 10);
        row.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.03, 1.03, row.ActualWidth / 2, _rowHeight / 2), new TranslateTransform() } };
        if (VisualTreeHelper.GetChildrenCount(row) > 0 && VisualTreeHelper.GetChild(row, 0) is Border bd)
            bd.Background = (Brush)FindResource("SelectedBrush");
        UpNextList.CaptureMouse();
    }

    private void UpdateDrag(Point p)
    {
        var dy = p.Y - _pressPoint.Y;
        var dragged = _dragRows[_dragFrom];
        ((TranslateTransform)((TransformGroup)dragged.RenderTransform).Children[1]).Y = dy;

        var target = Math.Clamp(_dragFrom + (int)Math.Round(dy / _rowHeight), 0, _dragRows.Count - 1);
        if (target == _dragTarget) return;
        _dragTarget = target;

        // Rows between the original and the target slide up or down by one row.
        for (var i = 0; i < _dragRows.Count; i++)
        {
            if (i == _dragFrom) continue;
            var shift = i > _dragFrom && i <= target ? -_rowHeight : i < _dragFrom && i >= target ? _rowHeight : 0;
            var anim = new DoubleAnimation(shift, new Duration(TimeSpan.FromMilliseconds(160))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ((TranslateTransform)_dragRows[i].RenderTransform).BeginAnimation(TranslateTransform.YProperty, anim);
        }
    }

    private void AutoScroll(double y)
    {
        const double edge = 36;
        if (y < edge) QueueScroller.ScrollToVerticalOffset(QueueScroller.VerticalOffset - 8);
        else if (y > QueueScroller.ActualHeight - edge) QueueScroller.ScrollToVerticalOffset(QueueScroller.VerticalOffset + 8);
    }

    private void EndDrag(bool commit)
    {
        _dragging = false;
        if (UpNextList.IsMouseCaptured) UpNextList.ReleaseMouseCapture();
        var from = _dragFrom;
        var to = _dragTarget;

        // Settle the dragged row into its slot, then apply the new order (which rebuilds the rows).
        if (from >= 0 && from < _dragRows.Count &&
            _dragRows[from].RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform move] })
        {
            var settle = new DoubleAnimation((to - from) * _rowHeight, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            settle.Completed += (_, _) =>
            {
                ResetRows();
                if (commit && to != from) Vm.MoveQueueItem(from, to);
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, settle.Duration));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, settle.Duration));
            move.BeginAnimation(TranslateTransform.YProperty, settle);
        }
        else
        {
            ResetRows();
        }
    }

    private void ResetRows()
    {
        foreach (var row in _dragRows)
        {
            row.RenderTransform = Transform.Identity;
            Panel.SetZIndex(row, 0);
            if (VisualTreeHelper.GetChildrenCount(row) > 0 && VisualTreeHelper.GetChild(row, 0) is Border bd) bd.ClearValue(Border.BackgroundProperty);
        }
        _dragRows.Clear();
    }

    // ---------------------------------------------------------------- seek bar

    private void SeekBar_MouseDown(object sender, MouseButtonEventArgs e) => Vm.IsSeeking = true;

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Let the slider update its value first.
        Dispatcher.BeginInvoke(() =>
        {
            Vm.SeekTo(sender is Slider s ? s.Value : SeekBar.Value);
            Vm.IsSeeking = false;
        });
    }

    // ---------------------------------------------------------------- lyrics

    private void Lyric_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LyricLine line }) return;
        _lyrics.Resync(); // jumping to a line follows the song again
        _cinemaLyrics.Resync();
        Vm.SeekToLyric(line);
    }
    /// <summary>
    /// The cinema lyrics fade out at the top and bottom edges of the view. A relative gradient would be
    /// stretched over all the lyrics (the whole song's height), so the first and last lines sat in the fade;
    /// this one is measured in pixels of what's on screen.
    /// </summary>
    private void CinemaLyricsScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = e.NewSize.Height;
        if (h <= 0) return;
        double top = Math.Min(56, h * 0.08), bottom = Math.Min(130, h * 0.16);
        var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, h) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, top / h));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - bottom / h));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        mask.Freeze();
        CinemaLyricsScroller.OpacityMask = mask;
    }

    private void LyricWord_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: LyricWord word } tb) tb.Foreground = LyricsAnimator.NewWordBrush(word);
    }

    // ---------------------------------------------------------------- cinema

    /// <summary>
    /// Cinema mode: the app's panels fade away and the now-playing view rises over the artwork backdrop
    /// (fade + a slight zoom settling in); leaving does the reverse.
    /// </summary>
    private void UpdateCinema()
    {
        var on = Vm.IsCinema;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = new Duration(TimeSpan.FromMilliseconds(on ? 420 : 300));
        if (Cinema.RenderTransform is not ScaleTransform { IsFrozen: false } zoom)
            Cinema.RenderTransform = zoom = new ScaleTransform(1, 1);
        Cinema.RenderTransformOrigin = new Point(0.5, 0.5);

        ShowCinemaChrome(true); // buttons start visible, then fade once the mouse rests
        if (!on) SetFullscreen(false);
        if (on)
        {
            Cinema.Visibility = Visibility.Visible;
            Cinema.IsHitTestVisible = true;
            Root.IsHitTestVisible = false;
            Cinema.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.04, 1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.04, 1, d) { EasingFunction = ease });
            var hide = new DoubleAnimation(0, d) { EasingFunction = ease };
            hide.Completed += (_, _) => { if (Vm.IsCinema) Root.Visibility = Visibility.Hidden; };
            Root.BeginAnimation(OpacityProperty, hide);
        }
        else
        {
            Root.Visibility = Visibility.Visible;
            Root.IsHitTestVisible = true;
            Cinema.IsHitTestVisible = false;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.03, d) { EasingFunction = ease });
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.03, d) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, d) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (!Vm.IsCinema) Cinema.Visibility = Visibility.Collapsed; };
            Cinema.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---------------------------------------------------------------- taskbar thumbnail buttons

    private System.Windows.Shell.ThumbButtonInfo? _thumbPlay, _thumbMute;

    /// <summary>
    /// Like Spotify: hovering Yesify's taskbar icon shows Previous, Play / Pause, Next and Mute under the
    /// window's preview.
    /// </summary>
    private void SetUpTaskbarButtons()
    {
        System.Windows.Shell.ThumbButtonInfo Button(char glyph, string tip, Action click)
        {
            var b = new System.Windows.Shell.ThumbButtonInfo { ImageSource = Yesify.Icon.ToImage(glyph, Brushes.White), Description = tip, DismissWhenClicked = false };
            b.Click += (_, _) => click();
            return b;
        }
        _thumbPlay = Button('\uE768', "Play", () => Vm.TogglePlayCommand.Execute(null));
        _thumbMute = Button('\uE767', "Mute", () => Vm.ToggleMuteCommand.Execute(null));
        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo
        {
            ThumbButtonInfos =
            {
                Button('\uE892', "Previous", () => Vm.PreviousCommand.Execute(null)),
                _thumbPlay,
                Button('\uE893', "Next", () => Vm.NextCommand.Execute(null)),
                _thumbMute,
            },
        };
        UpdateTaskbarButtons();
    }

    private void UpdateTaskbarButtons()
    {
        if (_thumbPlay == null || _thumbMute == null) return;
        _thumbPlay.ImageSource = Yesify.Icon.ToImage(Vm.IsPlaying ? '\uE769' : '\uE768', Brushes.White);
        _thumbPlay.Description = Vm.IsPlaying ? "Pause" : "Play";
        _thumbMute.ImageSource = Yesify.Icon.ToImage(Vm.IsMuted ? '\uE74F' : '\uE767', Brushes.White);
        _thumbMute.Description = Vm.IsMuted ? "Unmute" : "Mute";
    }

    // ---------------------------------------------------------------- warm songs under the pointer

    private DispatcherTimer? _warmTimer;
    private Track? _warmCandidate;

    /// <summary>
    /// Resting the pointer on a song for a moment starts finding its audio in the background (the slow
    /// part of starting a song), so a click on it plays almost at once.
    /// </summary>
    private void TrackRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Track track }) return;
        _warmCandidate = track;
        _warmTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(280), DispatcherPriority.Background, (_, _) =>
        {
            _warmTimer!.Stop();
            Vm.WarmTrack(_warmCandidate);
        }, Dispatcher);
        _warmTimer.Stop();
        _warmTimer.Start();
    }

    private void TrackRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Track track } && ReferenceEquals(track, _warmCandidate)) _warmTimer?.Stop();
    }

    // ---------------------------------------------------------------- cinema: buttons fade when the mouse rests

    // ---------------------------------------------------------------- cinema: full screen

    private bool _fullscreen;
    private bool _wasMaximized;
    private Rect _windowedBounds;

    private void CinemaFullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    /// <summary>
    /// Full screen (cinema only), the plain WPF way: the window is placed over the whole screen it's on
    /// (taskbar area included) and kept on top; leaving puts it back where it was (normal or maximized).
    /// Nothing about the window's frame or style is changed, so Windows never redraws an old-style frame.
    /// </summary>
    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        _fullscreen = on;
        if (on)
        {
            _wasMaximized = WindowState == WindowState.Maximized;
            _windowedBounds = _wasMaximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            // The screen the window is on, in device pixels, then in WPF units.
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* nearest */), ref info);
            var m = info.Monitor;
            var toWpf = target.TransformFromDevice;
            var topLeft = toWpf.Transform(new Point(m.Left, m.Top));
            var bottomRight = toWpf.Transform(new Point(m.Right, m.Bottom));
            if (_wasMaximized) WindowState = WindowState.Normal;
            Topmost = true;
            Left = topLeft.X;
            Top = topLeft.Y;
            Width = bottomRight.X - topLeft.X;
            Height = bottomRight.Y - topLeft.Y;
            SetCorners(hwnd, 1); // square corners at the screen's edges
        }
        else
        {
            Topmost = false;
            SetCorners(hwnd, 2);
            Left = _windowedBounds.Left;
            Top = _windowedBounds.Top;
            Width = _windowedBounds.Width;
            Height = _windowedBounds.Height;
            if (_wasMaximized) WindowState = WindowState.Maximized;
        }
        CinemaFullButton.Content = on ? "\uE73F" : "\uE740";
        CinemaFullButton.ToolTip = on ? "Exit full screen (Esc)" : "Full screen (F11)";
        UpdateMaximizedState();
    }

    private static void SetCorners(IntPtr hwnd, int preference) =>
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private DispatcherTimer? _cinemaIdle;
    private Point _cinemaMouse;

    private void Cinema_MouseMove(object sender, MouseEventArgs e)
    {
        // Ignore the tiny "moves" Windows reports without the mouse moving (e.g. when something redraws).
        var at = e.GetPosition(this);
        if ((at - _cinemaMouse).Length < 2) return;
        _cinemaMouse = at;
        ShowCinemaChrome(true);
    }

    /// <summary>
    /// Cinema's top buttons (and the pointer) show while the mouse moves and fade out after 2.5 s of rest,
    /// unless the pointer is on them.
    /// </summary>
    private void ShowCinemaChrome(bool show)
    {
        _cinemaIdle ??= new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Background, (_, _) =>
        {
            _cinemaIdle!.Stop();
            if (Vm.IsCinema && !CinemaChrome.IsMouseOver) ShowCinemaChrome(false);
        }, Dispatcher);
        _cinemaIdle.Stop();
        if (show && Vm.IsCinema) _cinemaIdle.Start();

        var visible = show || !Vm.IsCinema;
        if (CinemaChrome.IsHitTestVisible == visible && CinemaChrome.Tag is not null) return;
        CinemaChrome.Tag = true;
        CinemaChrome.IsHitTestVisible = visible;
        CinemaChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 180 : 450)));
        Cinema.Cursor = visible ? null : Cursors.None;
    }

    private void Status_Click(object sender, MouseButtonEventArgs e) => Vm.StatusText = "";

    // ---------------------------------------------------------------- settings

    private void SettingsBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => Vm.CloseSettingsCommand.Execute(null);

    private void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // clicks inside don't close

    // ---------------------------------------------------------------- window chrome

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizedState()
    {
        var max = WindowState == WindowState.Maximized;
        // A maximized borderless window overhangs the screen by the resize border.
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
        Backdrop.Margin = Root.Margin;
        Cinema.Margin = Root.Margin; // cinema's buttons sit where the normal title bar's do
        CinemaMaxButton.Content = max ? "" : ""; // restore / maximize
        CinemaMaxButton.ToolTip = max ? "Restore" : "Maximize";
        MaxButton.Content = max ? "" : "";
    }
}
