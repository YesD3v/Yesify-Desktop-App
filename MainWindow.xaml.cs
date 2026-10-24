using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            new CollectionContainer { Collection = vm.Tracks },
        };

        _lyrics = new LyricsAnimator(vm, LyricsList, LyricsScroller);
        LyricsList.IsVisibleChanged += (_, _) => _lyrics.Refresh();

        StateChanged += (_, _) => { UpdateMaximizedState(); RepaintAll(); };
        Activated += (_, _) => RepaintAll();
        IsVisibleChanged += (_, _) => RepaintAll();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSystemWake;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnSystemWake;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemWake;
        Vm.PropertyChanged += OnVmPropertyChanged;
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
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSystemWake;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnSystemWake;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemWake;
        _lyrics.Stop();
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
                break;
            case nameof(MainViewModel.IsRightPanelOpen):
                UpdateRightPanel();
                break;
            case nameof(MainViewModel.BackdropImageUrl):
                _ = UpdateBackdropAsync();
                break;
            case nameof(MainViewModel.SidebarCollapsed):
                UpdateSidebarWidth();
                break;
        }
    }

    // ---------------------------------------------------------------- glass backdrop

    /// <summary>Cross-fades to the new artwork and tints the window with its colors (glass mode only).</summary>
    private async Task UpdateBackdropAsync()
    {
        if (Vm.IsLightweight)
        {
            // Nothing to draw; let the images go.
            _backdropUrl = null;
            BackA.Source = BackB.Source = null;
            return;
        }

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

        TintA.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(backdrop.A, duration));
        TintB.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(backdrop.B, duration));
        TintC.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(backdrop.C, duration));
    }

    // ---------------------------------------------------------------- right panel

    private void UpdateRightPanel()
    {
        if (Vm.IsRightPanelOpen)
        {
            RightColumn.MinWidth = 260;
            RightColumn.Width = new GridLength(Math.Clamp(Vm.RightPanelWidth, 260, 640));
        }
        else
        {
            RightColumn.MinWidth = 0;
            RightColumn.Width = new GridLength(0);
        }
    }

    private void RightSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        Vm.RightPanelWidth = RightColumn.ActualWidth;

    // ---------------------------------------------------------------- keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.Escape && (Vm.IsSettingsOpen || Vm.IsEditingPlaylist || Vm.IsImporting))
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
    private MenuItem AddToPlaylistMenu(string header, Func<List<Track>> tracks)
    {
        var menu = new MenuItem { Header = header };
        menu.Items.Add(Item("New Playlist…", () => Vm.BeginNewPlaylist(tracks())));
        var targets = Vm.UserPlaylists.Where(p => p != Vm.CurrentUserPlaylist).ToList();
        var imported = Vm.ImportedPlaylists.Where(p => p.Id != Vm.CurrentCollection?.Id).ToList();
        if (targets.Count + imported.Count > 0) menu.Items.Add(new Separator());
        foreach (var p in targets) menu.Items.Add(Item(p.Name, () => Vm.AddToPlaylist(p, tracks())));
        foreach (var p in imported) menu.Items.Add(Item(p.Name, () => _ = Vm.AddToImportedAsync(p, tracks())));
        return menu;
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
        menu.Items.Add(Item("Play Next", () => Vm.PlayNextCommand.Execute(track)));
        menu.Items.Add(Item("Add to Queue", () => Vm.AddToQueueCommand.Execute(track)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(track.IsLiked ? "Remove from Liked Songs" : "Add to Liked Songs", () => Vm.ToggleLikeCommand.Execute(track)));
        menu.Items.Add(AddToPlaylistMenu("Add to Playlist", () => [track]));
        if (Vm.CurrentUserPlaylist != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove from this Playlist", () => Vm.RemoveFromCurrentPlaylist(track)));
        }
    }

    /// <summary>"…" on an album / playlist / Liked Songs: everything in it goes somewhere.</summary>
    private void HeaderMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var all = Vm.ShownTracks.ToList();
        menu.Items.Add(AddToPlaylistMenu("Add All to Playlist", () => all));
        menu.Items.Add(Item("Copy to New Playlist…", Vm.CopyViewToNewPlaylist, all.Count > 0));
        menu.Items.Add(Item("Add All to Queue", () => { foreach (var t in all) Vm.AddToQueueCommand.Execute(t); }, all.Count > 0));
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

    private void LibraryTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryItem item }) Vm.OpenLibraryItemCommand.Execute(item);
    }

    private void LibraryTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryItem item, ContextMenu: { } menu }) return;
        menu.Items.Clear();
        switch (item.Kind)
        {
            case LibraryItemKind.UserPlaylist when Vm.UserPlaylists.FirstOrDefault(p => p.Id == item.Id) is { } p:
                menu.Items.Add(Item("Open", () => Vm.OpenLibraryItemCommand.Execute(item)));
                menu.Items.Add(Item("Edit Details…", () => Vm.BeginEditPlaylist(p)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete Playlist", () => ConfirmDelete(p)));
                break;
            case LibraryItemKind.Saved when item.Saved != null:
                menu.Items.Add(Item("Open", () => Vm.OpenLibraryItemCommand.Execute(item)));
                menu.Items.Add(Item("Edit…", () => _ = Vm.EditImportedAsync(item.Saved)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Remove from Library", () => Vm.RemoveSavedCommand.Execute(item.Saved)));
                break;
            default:
                e.Handled = true; // Liked Songs / New / Import: nothing to manage
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
        BeginAnimation(SidebarWidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
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
    private void QueueMenuPlay_Click(object sender, RoutedEventArgs e) =>
        Vm.PlayQueueItemCommand.Execute((sender as FrameworkElement)?.DataContext as QueueItem);

    private void QueueMenuRemove_Click(object sender, RoutedEventArgs e) =>
        Vm.RemoveQueueItemCommand.Execute((sender as FrameworkElement)?.DataContext as QueueItem);

    // ---------------------------------------------------------------- seek bar

    private void SeekBar_MouseDown(object sender, MouseButtonEventArgs e) => Vm.IsSeeking = true;

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Let the slider update its value first.
        Dispatcher.BeginInvoke(() =>
        {
            Vm.SeekTo(SeekBar.Value);
            Vm.IsSeeking = false;
        });
    }

    // ---------------------------------------------------------------- lyrics

    private void Lyric_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LyricLine line }) Vm.SeekToLyric(line);
    }
    private void LyricWord_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: LyricWord word } tb) tb.Foreground = LyricsAnimator.NewWordBrush(word);
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
        MaxButton.Content = max ? "" : "";
    }
}
