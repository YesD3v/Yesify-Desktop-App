using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using Yesify.Models;
using Yesify.Services;

namespace Yesify.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string SettingsFile = "settings.json";

    private readonly Settings _settings = Storage.Load<Settings>(SettingsFile);
    private readonly SpotifyEmbedService _spotify = new();
    private readonly CoverService _covers = new();
    private readonly LikedService _liked = new();
    private readonly YouTubeService _youtube = new();
    private readonly LyricsService _lyrics = new();
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _timer;

    // Playback queue: the collection playback started from, plus an optional shuffled order.
    private List<Track> _context = [];
    private List<int> _order = [];
    private int _orderPos = -1;
    private readonly List<Track> _upNext = [];
    private int _failures;

    private bool _viewIsLiked;
    private CancellationTokenSource? _viewCts;
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _lyricsCts;

    public MainViewModel()
    {
        _player.Volume = (float)_settings.Volume;
        _volume = _settings.Volume;
        _showLyrics = _settings.ShowLyrics;
        _showQueue = _settings.ShowQueue && !_settings.ShowLyrics;
        _player.TrackEnded += (_, _) => OnTrackEnded();
        _player.PlaybackFailed += (_, ex) => _ = SkipBrokenTrackAsync(ex.Message);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick(); // started by SetUiVisible

        Tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTracks));
        RefreshSidebar();
        RestoreSession();
        RefreshQueue(); // the queue panel may be open from last time: fill it with the restored session
        LoadHome();
    }

    // ================================================================ bindable state

    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];
    public ObservableCollection<Track> Tracks { get; } = [];
    public bool HasTracks => Tracks.Count > 0;
    public bool HasPlaylists => Playlists.Count > 0;
    public ObservableCollection<LyricLine> Lyrics { get; } = [];

    [ObservableProperty] private string _viewTitle = "";
    [ObservableProperty] private string _viewSubtitle = "";
    [ObservableProperty] private string? _viewImageUrl;
    [ObservableProperty] private bool _viewIsLikedHeader;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private PlaylistInfo? _selectedPlaylist;
    [ObservableProperty] private PlaylistInfo? _currentCollection;

    [ObservableProperty] private Track? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private RepeatMode _repeat;
    [ObservableProperty] private bool _showLyrics;
    [ObservableProperty] private int _currentLyricIndex = -1;
    [ObservableProperty] private LyricLine? _currentLyric;
    [ObservableProperty] private string _lyricsStatus = "Play a song to see its lyrics.";

    /// <summary>For the tray menu; reads the player directly since UI state isn't updated while hidden.</summary>
    public bool IsAudioPlaying => _player.IsPlaying;

    /// <summary>
    /// The UI-refresh timer only runs while a window shows it, so with the window closed nothing ticks
    /// besides the audio itself.
    /// </summary>
    public void SetUiVisible(bool visible)
    {
        if (visible)
        {
            Tick();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    // ================================================================ right panel (lyrics / queue)

    [ObservableProperty] private bool _showQueue;

    public bool IsRightPanelOpen => ShowLyrics || ShowQueue;

    /// <summary>Width of the lyrics/queue panel; the view resizes it and saves it here.</summary>
    public double RightPanelWidth
    {
        get => _settings.RightPanelWidth;
        set
        {
            _settings.RightPanelWidth = value;
            SaveSettings();
        }
    }

    // The two panels share one slot: opening one closes the other.
    partial void OnShowLyricsChanged(bool value)
    {
        if (value) ShowQueue = false;
        _settings.ShowLyrics = value;
        SaveSettings();
        OnPropertyChanged(nameof(IsRightPanelOpen));
    }

    partial void OnShowQueueChanged(bool value)
    {
        if (value)
        {
            ShowLyrics = false;
            RefreshQueue();
        }
        _settings.ShowQueue = value;
        SaveSettings();
        OnPropertyChanged(nameof(IsRightPanelOpen));
    }

    [RelayCommand]
    private void ToggleLyrics() => ShowLyrics = !ShowLyrics;

    [RelayCommand]
    private void ToggleQueue() => ShowQueue = !ShowQueue;

    // ================================================================ queue

    private readonly List<Track> _history = [];

    /// <summary>Queued songs first, then what's coming up in the playlist.</summary>
    public ObservableCollection<QueueItem> UpNextView { get; } = [];

    /// <summary>Most recent first.</summary>
    public ObservableCollection<QueueItem> HistoryView { get; } = [];

    /// <summary>The queue shows this many Up next rows before a "Show more" button.</summary>
    private const int QueuePageSize = 50;

    /// <summary>Set once the user taps "Show more", so the whole queue is listed.</summary>
    [ObservableProperty] private bool _queueExpanded;

    /// <summary>True when Up next has more songs than are currently shown.</summary>
    public bool HasMoreUpNext { get; private set; }

    // ---------------------------------------------------------------- saved session

    private const string SessionFile = "session.json";
    private DispatcherTimer? _sessionTimer;
    private Track? _resumeTrack;
    private TimeSpan _resumeAt;

    /// <summary>Saves the session shortly after a change (changes often come in bursts).</summary>
    private void ScheduleSessionSave()
    {
        _sessionTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => { _sessionTimer!.Stop(); SaveSession(); }, Dispatcher.CurrentDispatcher);
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSession()
    {
        var state = new SessionState
        {
            // Copies: the file is written on a background thread while these lists keep changing.
            Context = _context.ToList(),
            Order = _order.ToList(),
            OrderPos = _orderPos,
            UpNext = _upNext.ToList(),
            History = _history.ToList(),
            Current = CurrentTrack,
            PositionSeconds = _player.HasTrack ? _player.Position.TotalSeconds : _resumeAt.TotalSeconds,
            Shuffle = Shuffle,
            Repeat = Repeat,
        };
        Storage.SaveInBackground(SessionFile, state);
    }

    /// <summary>Puts the last session back (paused); play continues from the saved position.</summary>
    private void RestoreSession()
    {
        var s = Storage.Load<SessionState>(SessionFile);
        if (s.Current == null) return;

        _context = s.Context;
        var valid = s.Order.Count == _context.Count && s.Order.All(i => i >= 0 && i < _context.Count);
        _order = valid ? s.Order : Enumerable.Range(0, _context.Count).ToList();
        _orderPos = Math.Clamp(s.OrderPos, -1, _order.Count - 1);
        _upNext.AddRange(s.UpNext);
        _history.AddRange(s.History);
        Shuffle = s.Shuffle;
        Repeat = s.Repeat;

        foreach (var t in _context.Concat(_upNext).Concat(_history).Append(s.Current))
        {
            t.IsLiked = _liked.Contains(t);
            if (t.SpotifyId != null) t.YouTubeId = null; // re-matched (and cached) when played
        }
        CurrentTrack = s.Current;
        _resumeTrack = s.Current;
        _resumeAt = TimeSpan.FromSeconds(Math.Max(0, s.PositionSeconds));
        DurationSeconds = Math.Max(1, s.Current.Duration.TotalSeconds);
        DurationText = s.Current.DurationText;
        PositionSeconds = _resumeAt.TotalSeconds;
        PositionText = Track.Format(_resumeAt);
        LyricsStatus = "Press play to continue.";
    }

    private void RefreshQueue()
    {
        ScheduleSessionSave();
        if (!ShowQueue) return; // only maintained while someone is looking at it

        var cap = QueueExpanded ? int.MaxValue : QueuePageSize;
        var total = _upNext.Count + Math.Max(0, _order.Count - _orderPos - 1);

        UpNextView.Clear();
        for (var i = 0; i < _upNext.Count && UpNextView.Count < cap; i++)
            UpNextView.Add(new QueueItem(_upNext[i], QueueSource.Queued, i));
        for (var p = _orderPos + 1; p < _order.Count && UpNextView.Count < cap; p++)
            UpNextView.Add(new QueueItem(_context[_order[p]], QueueSource.Context, p));

        HasMoreUpNext = total > UpNextView.Count;
        OnPropertyChanged(nameof(HasMoreUpNext));

        HistoryView.Clear();
        for (var i = 0; i < _history.Count; i++) HistoryView.Add(new QueueItem(_history[i], QueueSource.History, i));
    }

    [RelayCommand]
    private void ShowMoreQueue()
    {
        QueueExpanded = true;
        RefreshQueue();
    }

    [RelayCommand]
    private void PlayNext(Track? track)
    {
        if (track == null) return;
        _upNext.Insert(0, track);
        StatusText = $"“{track.Title}” plays next.";
        if (CurrentTrack == null) _ = PlayNextAsync(manual: true);
        RefreshQueue();
    }

    [RelayCommand]
    private void PlayQueueItem(QueueItem? item)
    {
        if (item == null) return;
        switch (item.Source)
        {
            case QueueSource.Queued when item.Index < _upNext.Count:
                _upNext.RemoveAt(item.Index);
                break;
            case QueueSource.Context when item.Index < _order.Count:
                _orderPos = item.Index; // skip ahead in the playlist
                break;
        }
        _ = PlayTrackAsync(item.Track);
    }

    /// <summary>
    /// Moves an Up next row from one position to another. Everything up to the last song you queued
    /// becomes your queue (in the new order); playlist songs after that keep playing from the playlist,
    /// in the new order.
    /// </summary>
    public void MoveQueueItem(int from, int to)
    {
        var shown = UpNextView.ToList();
        if (from < 0 || from >= shown.Count || to < 0 || to >= shown.Count || from == to) return;
        var moved = shown[from];
        shown.RemoveAt(from);
        shown.Insert(to, moved);

        var lastQueued = shown.FindLastIndex(x => x.Source == QueueSource.Queued);
        var queuedPart = shown.Take(lastQueued + 1).ToList();
        var playlistPart = shown.Skip(lastQueued + 1).Where(x => x.Source == QueueSource.Context).Select(x => _order[x.Index]).ToList();

        // Rebuild the play order: what's already played, the shown playlist songs in their new order,
        // then the rest of the playlist past the shown window. Songs pulled into the queue leave the order.
        var shownPositions = UpNextView.Where(x => x.Source == QueueSource.Context).Select(x => x.Index).ToList();
        var lastShownPos = shownPositions.Count > 0 ? shownPositions.Max() : _orderPos;
        _order = _order.Take(_orderPos + 1).Concat(playlistPart).Concat(_order.Skip(lastShownPos + 1)).ToList();

        _upNext.Clear();
        _upNext.AddRange(queuedPart.Select(x => x.Track));
        RefreshQueue();
        _youtube.Prefetch(PeekNext());
    }

    [RelayCommand]
    private void RemoveQueueItem(QueueItem? item)
    {
        if (item is not { Source: QueueSource.Queued } || item.Index >= _upNext.Count) return;
        _upNext.RemoveAt(item.Index);
        RefreshQueue();
    }

    // ================================================================ import

    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importText = "";
    private bool _saveNextCollection;

    [RelayCommand]
    private void BeginImport()
    {
        ImportText = "";
        IsImporting = true;
    }

    [RelayCommand]
    private void CancelImport() => IsImporting = false;

    /// <summary>Opens the pasted link; playlists and albums are also saved to the sidebar.</summary>
    [RelayCommand]
    private async Task ConfirmImportAsync()
    {
        var text = ImportText.Trim();
        IsImporting = false;
        if (text.Length == 0) return;

        var isLink = SpotifyEmbedService.ParseLink(text) != null ||
                     text.Contains("youtu", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("list=", StringComparison.OrdinalIgnoreCase);
        if (!isLink)
        {
            StatusText = "Paste a Spotify or YouTube playlist link.";
            return;
        }

        _saveNextCollection = true;
        SearchText = text;
        await SearchAsync();
        _saveNextCollection = false;
    }

    // ================================================================ settings

    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private string _cacheSizeText = "";

    [RelayCommand]
    private void OpenSettings()
    {
        CacheSizeText = $"{FormatSize(YouTubeService.CacheSize())} of {FormatSize(YouTubeService.MaxCacheBytes)}";
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void ClearAudioCache()
    {
        YouTubeService.ClearCache();
        CacheSizeText = $"{FormatSize(YouTubeService.CacheSize())} of {FormatSize(YouTubeService.MaxCacheBytes)}";
        StatusText = "Downloaded audio cleared.";
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    // ================================================================ appearance

    /// <summary>Lightweight: flat dark UI drawn in software. Off: the glass look with artwork backgrounds.</summary>
    public bool IsLightweight
    {
        get => _settings.Lightweight;
        set
        {
            if (_settings.Lightweight == value) return;
            _settings.Lightweight = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGlass));
        }
    }

    public bool IsGlass => !IsLightweight;

    /// <summary>Discord Rich Presence: the song, artist, cover and progress on your Discord profile.</summary>
    public bool ShowOnDiscord
    {
        get => _settings.DiscordRpc;
        set
        {
            if (_settings.DiscordRpc == value) return;
            _settings.DiscordRpc = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>The artwork the glass background is made from: the open playlist/album, else the current song.</summary>
    public string? BackdropImageUrl => CurrentTrack?.ImageUrl ?? CurrentCollection?.ImageUrl ?? ViewImageUrl;

    /// <summary>Exact playback position, for per-frame lyric animation.</summary>
    public TimeSpan AudioPosition => _player.SmoothPosition;

    /// <summary>Where playback is, read straight from the player (also while no window shows it).</summary>
    public TimeSpan PlaybackPosition => _player.HasTrack ? _player.Position : _resumeAt;

    /// <summary>Raised after a jump in the song, so the Windows media session can show the new position.</summary>
    public event Action? Seeked;

    /// <summary>The position the active-lyric index is computed from (decoder position plus a small lead).</summary>
    public TimeSpan LyricIndexPosition => _player.Position + TimeSpan.FromMilliseconds(250);

    partial void OnViewImageUrlChanged(string? value) => OnPropertyChanged(nameof(BackdropImageUrl));

    // Follow the playing song's cover too: playlist covers can arrive after the song starts.
    partial void OnCurrentTrackChanged(Track? oldValue, Track? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnCurrentTrackPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnCurrentTrackPropertyChanged;
        OnPropertyChanged(nameof(BackdropImageUrl));
    }

    private void OnCurrentTrackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Track.ImageUrl)) OnPropertyChanged(nameof(BackdropImageUrl));
    }

    // ================================================================ miniplayer

    /// <summary>Handled by App, which owns the windows.</summary>
    public event Action? MiniPlayerRequested;
    public event Action? FullPlayerRequested;

    [RelayCommand]
    private void OpenMiniPlayer() => MiniPlayerRequested?.Invoke();

    [RelayCommand]
    private void OpenFullPlayer() => FullPlayerRequested?.Invoke();

    public (double? Left, double? Top, double Width, double Height) MiniBounds =>
        (_settings.MiniLeft, _settings.MiniTop, _settings.MiniWidth, _settings.MiniHeight);

    public void SaveMiniBounds(double left, double top, double width, double height)
    {
        _settings.MiniLeft = left;
        _settings.MiniTop = top;
        _settings.MiniWidth = width;
        _settings.MiniHeight = height;
        SaveSettings();
    }

    /// <summary>True the first time only, so the "still playing in the tray" hint shows once.</summary>
    public bool ConsumeTrayHint()
    {
        if (_settings.TrayHintShown) return false;
        _settings.TrayHintShown = true;
        SaveSettings();
        return true;
    }

    /// <summary>Set by the view while the user drags the seek bar.</summary>
    public bool IsSeeking { get; set; }

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = (float)value;
        _settings.Volume = value;
    }

    private DispatcherTimer? _statusTimer;

    // Messages fade out on their own; a new message restarts the clock.
    partial void OnStatusTextChanged(string value)
    {
        _statusTimer?.Stop();
        if (value.Length == 0) return;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Tick += (_, _) => { _statusTimer?.Stop(); StatusText = ""; };
        _statusTimer.Start();
    }

    partial void OnSelectedPlaylistChanged(PlaylistInfo? value)
    {
        if (value != null) _ = OpenPlaylistAsync(value);
    }

    private void SaveSettings()
    {
        try { Storage.Save(SettingsFile, _settings); } catch { /* not fatal */ }
    }

    // ================================================================ liked songs

    [RelayCommand]
    private void ToggleLike(Track? track)
    {
        if (track == null) return;
        var like = !track.IsLiked;
        if (like) _liked.Add(track); else _liked.Remove(track);

        // The same song can be on screen, in the queue and in the player as different objects.
        foreach (var t in Tracks.Concat(_context).Concat(_upNext).Concat(_liked.Tracks).Append(CurrentTrack))
            if (t != null && t.Key == track.Key) t.IsLiked = like;
        if (CurrentTrack != null && CurrentTrack.Key == track.Key) CurrentTrack.IsLiked = like;

        if (CurrentView == ViewMode.Library) RefreshLibrary(); // Liked Songs count
        if (_viewIsLiked)
        {
            if (like) Tracks.Insert(0, _liked.Tracks[0]);
            else foreach (var t in Tracks.Where(t => t.Key == track.Key).ToList()) Tracks.Remove(t);
            Renumber();
            ViewSubtitle = $"{Tracks.Count} songs · saved on this PC";
        }
    }

    [RelayCommand]
    private Task OpenLikedAsync()
    {
        SelectedPlaylist = null;
        return LoadViewAsync(_ =>
        {
            _viewIsLiked = true;
            var tracks = _liked.Tracks.ToList();
            return Task.FromResult(("Liked Songs", $"{tracks.Count} songs · saved on this PC", (string?)null, tracks));
        });
    }

    // ================================================================ sidebar

    /// <summary>Sidebar = playlists and albums the user saved from links.</summary>
    private void RefreshSidebar()
    {
        Playlists.Clear();
        foreach (var p in _settings.SavedCollections) Playlists.Add(p);
        OnPropertyChanged(nameof(HasPlaylists));
        RefreshLibrary();
    }

    public bool IsCurrentSaved => CurrentCollection != null && _settings.SavedCollections.Any(s => s.Id == CurrentCollection.Id);

    partial void OnCurrentCollectionChanged(PlaylistInfo? value)
    {
        OnPropertyChanged(nameof(IsCurrentSaved));
        OnPropertyChanged(nameof(BackdropImageUrl));
        if (value != null && _saveNextCollection)
        {
            _saveNextCollection = false;
            if (!IsCurrentSaved) ToggleSaveCurrent();
        }
    }

    [RelayCommand]
    private void ToggleSaveCurrent()
    {
        if (CurrentCollection is not { } current) return;
        if (IsCurrentSaved)
        {
            _settings.SavedCollections.RemoveAll(s => s.Id == current.Id);
            _library.ForgetImported(current.Id);
            StatusText = $"Removed “{current.Name}” from your library.";
        }
        else
        {
            _settings.SavedCollections.Insert(0, current);
            StatusText = $"Added “{current.Name}” to your library.";
        }
        SaveSettings();
        OnPropertyChanged(nameof(IsCurrentSaved));
        RememberImportedTracks();
        RefreshSidebar();
    }

    [RelayCommand]
    private void RemoveSaved(PlaylistInfo? p)
    {
        if (p == null || _settings.SavedCollections.RemoveAll(s => s.Id == p.Id) == 0) return;
        _library.ForgetImported(p.Id);
        SaveSettings();
        RefreshSidebar();
        OnPropertyChanged(nameof(IsCurrentSaved));
    }

    // ================================================================ browsing

    private void Renumber()
    {
        for (var i = 0; i < Tracks.Count; i++) Tracks[i].Number = i + 1;
    }

    /// <summary>Runs a view load, cancelling whatever load was in progress.</summary>
    /// <param name="more">Optional follow-up that keeps adding to the view (e.g. the rest of a long playlist).</param>
    private async Task LoadViewAsync(
        Func<CancellationToken, Task<(string Title, string Subtitle, string? Image, List<Track> Tracks)>> load,
        Func<CancellationToken, Task>? more = null)
    {
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        IsLoading = true;
        CurrentCollection = null;
        CurrentUserPlaylist = null;
        ViewDescription = "";
        CurrentView = ViewMode.Collection;
        _viewIsLiked = false;
        Tracks.Clear();
        try
        {
            var (title, subtitle, image, tracks) = await load(cts.Token);
            if (cts.IsCancellationRequested) return;
            ViewTitle = title;
            ViewSubtitle = subtitle;
            ViewImageUrl = image;
            ViewIsLikedHeader = _viewIsLiked;
            for (var i = 0; i < tracks.Count; i++)
            {
                tracks[i].Number = i + 1;
                tracks[i].IsLiked = _liked.Contains(tracks[i]);
                Tracks.Add(tracks[i]);
            }
            IsLoading = false;

            await Task.WhenAll(_covers.FillAsync(tracks, cts.Token), more?.Invoke(cts.Token) ?? Task.CompletedTask);
            if (!cts.IsCancellationRequested) RememberImportedTracks();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) StatusText = ex.Message;
        }
        finally
        {
            if (_viewCts == cts) IsLoading = false;
        }
    }

    private Task OpenPlaylistAsync(PlaylistInfo p)
    {
        if (p.Kind == CollectionKind.YouTubePlaylist && PlaylistId.TryParse(p.Id) is { } ytId)
            return OpenYouTubePlaylistAsync(ytId);
        return OpenSpotifyCollectionAsync(p.Kind == CollectionKind.Album ? "album" : "playlist", p.Id);
    }

    private Task OpenSpotifyCollectionAsync(string kind, string id)
    {
        PlaylistInfo? info = null;
        return LoadViewAsync(async ct =>
        {
            var result = await _spotify.GetAsync(kind, id, ct);
            info = result.Info;
            CurrentCollection = info;
            return (info.Name, $"{info.Owner} · {result.Tracks.Count} songs", info.ImageUrl, result.Tracks);
        }, kind == "playlist" ? ct => LoadRestOfPlaylistAsync(info!, ct) : null);
    }

    /// <summary>
    /// The embed page stops at 100 songs; fetch the full list and append whatever is missing, in batches.
    /// </summary>
    private async Task LoadRestOfPlaylistAsync(PlaylistInfo info, CancellationToken ct)
    {
        try
        {
            var uris = await _spotify.GetPlaylistUrisAsync(info.Id, ct);
            var have = Tracks.Select(t => t.SpotifyId).OfType<string>().ToHashSet();
            var missing = uris
                .Where(u => !u.StartsWith("spotify:episode:"))
                .Where(u => !(u.StartsWith("spotify:track:") && have.Contains(u["spotify:track:".Length..])))
                .ToList();
            if (missing.Count == 0) return;

            var total = Tracks.Count + missing.Count;
            for (var i = 0; i < missing.Count; i += 50)
            {
                ViewSubtitle = $"{info.Owner} · loading songs… {Tracks.Count} of {total}";
                var batch = await _spotify.GetTracksAsync(missing.Skip(i).Take(50).ToList(), info.Id, ct);
                ct.ThrowIfCancellationRequested();
                foreach (var t in batch)
                {
                    t.Number = Tracks.Count + 1;
                    t.IsLiked = _liked.Contains(t);
                    Tracks.Add(t);
                }
            }
            ViewSubtitle = $"{info.Owner} · {Tracks.Count} songs";
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Undocumented backend; keep what the embed page gave us.
            if (Tracks.Count >= SpotifyEmbedService.PlaylistCap - 5)
                ViewSubtitle = $"{info.Owner} · first {Tracks.Count} songs (Spotify wouldn't share the rest right now)";
        }
    }

    private Task OpenYouTubePlaylistAsync(PlaylistId id) => LoadViewAsync(async ct =>
    {
        var (info, tracks) = await _youtube.GetPlaylistAsync(id, ct);
        CurrentCollection = info;
        return (info.Name, $"{info.Owner} · {tracks.Count} videos", info.ImageUrl, tracks);
    });

    [RelayCommand]
    private async Task SearchAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;
        SelectedPlaylist = null;

        if (SpotifyEmbedService.ParseLink(text) is { } link)
        {
            switch (link.Kind)
            {
                case "playlist":
                case "album":
                    await OpenSpotifyCollectionAsync(link.Kind, link.Id);
                    return;
                case "track":
                    await LoadViewAsync(async ct =>
                    {
                        var t = (await _spotify.GetAsync("track", link.Id, ct)).Tracks[0];
                        return (t.Title, t.Artist, t.ImageUrl, new List<Track> { t });
                    });
                    if (Tracks.Count == 1) PlayFrom(Tracks[0]);
                    return;
                default:
                    StatusText = $"Spotify {link.Kind} links aren't supported. Try a playlist, album or track link.";
                    return;
            }
        }

        if (text.Contains("list=", StringComparison.OrdinalIgnoreCase) && PlaylistId.TryParse(text) is { } ytPlaylist)
        {
            await OpenYouTubePlaylistAsync(ytPlaylist);
            return;
        }

        if (text.Contains("youtu", StringComparison.OrdinalIgnoreCase) && VideoId.TryParse(text) is { } videoId)
        {
            await LoadViewAsync(async ct =>
            {
                var t = await _youtube.GetVideoAsync(videoId, ct);
                return (t.Title, t.Artist, t.ImageUrl, new List<Track> { t });
            });
            if (Tracks.Count == 1) PlayFrom(Tracks[0]);
            return;
        }

        await LoadViewAsync(async ct =>
        {
            var tracks = await _youtube.SearchAsync(text, ct);
            return ($"“{text}”", $"{tracks.Count} results", null, tracks);
        });
    }
    // ================================================================ playback

    [RelayCommand]
    private void PlayFrom(Track? track)
    {
        if (track == null) return;
        StartContext(Tracks.ToList(), Math.Max(0, Tracks.IndexOf(track)));
        EnsureFullContext(CurrentCollection);
    }

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count == 0) return;
        StartContext(Tracks.ToList(), Shuffle ? -1 : 0);
        EnsureFullContext(CurrentCollection);
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        if (Tracks.Count == 0) return;
        Shuffle = true;
        StartContext(Tracks.ToList(), -1);
        EnsureFullContext(CurrentCollection);
    }

    [RelayCommand]
    private void AddToQueue(Track? track)
    {
        if (track == null) return;
        _upNext.Add(track);
        StatusText = $"Queued “{track.Title}”.";
        if (CurrentTrack == null) _ = PlayNextAsync(manual: true);
        RefreshQueue();
    }

    /// <summary>
    /// Adds a whole list of songs (a playlist, an album, Liked Songs) to the end of the queue, in order
    /// or shuffled. Shuffled uses the same draw as shuffle play: what you haven't heard lately comes first.
    /// </summary>
    /// <param name="next">Put them at the front of the queue ("Play Next") instead of the end.</param>
    public void QueueAll(IReadOnlyList<Track> tracks, bool shuffled, string? name = null, bool next = false)
    {
        if (tracks.Count == 0)
        {
            StatusText = name != null ? $"“{name}” has no songs to queue." : "Nothing to queue.";
            return;
        }
        var list = tracks.ToList();
        if (shuffled)
        {
            var draw = FreshDraw();
            list = list.OrderByDescending(draw).ToList();
        }
        foreach (var t in list) t.IsLiked = _liked.Contains(t);
        if (next) _upNext.InsertRange(0, list);
        else _upNext.AddRange(list);

        var count = list.Count == 1 ? "1 song" : $"{list.Count} songs";
        var what = name != null ? $"“{name}” ({count})" : list.Count == 1 ? $"“{list[0].Title}”" : count;
        StatusText = next ? $"{what} plays next." : shuffled ? $"Queued {what}, shuffled." : $"Queued {what}.";
        if (CurrentTrack == null) _ = PlayNextAsync(manual: true);
        RefreshQueue();
        _youtube.Prefetch(PeekNext());
    }

    /// <summary>
    /// The songs behind a Library tile, without opening it. Imported playlists come from the open view or
    /// the remembered copy if there is one; otherwise from Spotify / YouTube. Null if they couldn't be loaded.
    /// </summary>
    public async Task<List<Track>?> LibraryItemTracksAsync(LibraryItem item)
    {
        switch (item.Kind)
        {
            case LibraryItemKind.Liked:
                return _liked.Tracks.ToList();
            case LibraryItemKind.UserPlaylist when _library.Find(item.Id!) is { } p:
                return p.Tracks.ToList();
            case LibraryItemKind.Saved when item.Saved is { } saved:
                var songs = CurrentCollection?.Id == saved.Id && !IsLoading ? Tracks.ToList() : _library.ImportedTracks(saved.Id);
                if (songs != null) return songs;
                StatusText = $"Loading “{saved.Name}”…";
                try
                {
                    songs = await FetchCollectionTracksAsync(saved);
                    await _covers.FillAsync(songs, CancellationToken.None);
                    StatusText = "";
                    return songs;
                }
                catch (Exception ex)
                {
                    StatusText = $"Couldn't load “{saved.Name}”: {ex.Message}";
                    return null;
                }
        }
        return null;
    }

    /// <summary>Queues a playlist straight from its Library tile, without opening it.</summary>
    public async Task QueueLibraryItemAsync(LibraryItem item, bool shuffled)
    {
        if (await LibraryItemTracksAsync(item) is { } songs) QueueAll(songs, shuffled, item.Title);
    }

    /// <summary>Starts playing a list of songs (from a menu, without opening it), from the top or shuffled.</summary>
    public void PlayTracks(IReadOnlyList<Track> tracks, bool shuffled)
    {
        if (tracks.Count == 0) return;
        if (shuffled) Shuffle = true;
        foreach (var t in tracks) t.IsLiked = _liked.Contains(t);
        StartContext(tracks.ToList(), Shuffle ? -1 : 0);
    }

    public bool IsLiked(Track track) => _liked.Contains(track);

    /// <param name="startIndex">The song to start with, or -1 to let the shuffle pick one.</param>
    private void StartContext(List<Track> tracks, int startIndex)
    {
        _contextFillCts?.Cancel(); // drop any background fill from the previous context
        _radioExhausted = false;
        QueueExpanded = false;
        _context = tracks;
        _upNext.Clear();
        if (startIndex < 0) startIndex = FreshShuffle(Enumerable.Range(0, _context.Count))[0];
        BuildOrder(startIndex);
        _ = PlayTrackAsync(_context[_order[_orderPos]]);
    }

    /// <summary>
    /// A Spotify playlist's embed page only lists its first 100 songs; the rest stream into the open view
    /// afterwards. If playback started before they arrived, the context would be just those 100 and loop.
    /// This fetches the complete list in the background and appends whatever is missing to the context, so
    /// a long playlist plays all the way through. Scoped to Spotify playlists — the only paginated case.
    /// </summary>
    private CancellationTokenSource? _contextFillCts;

    private void EnsureFullContext(PlaylistInfo? collection)
    {
        _contextFillCts?.Cancel();
        if (collection is not { Kind: CollectionKind.Playlist }) return;

        var cts = _contextFillCts = new CancellationTokenSource();
        var startedFrom = _context; // the exact list this fill is for
        var ui = Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                var all = await FetchCollectionTracksAsync(collection);
                await ui.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested && ReferenceEquals(_context, startedFrom))
                        AppendToContext(all);
                });
            }
            catch { /* keep whatever the view already loaded */ }
        });
    }

    /// <summary>Adds songs not already in the context to the end of the play order (or interspersed, when shuffled).</summary>
    private void AppendToContext(IReadOnlyList<Track> tracks)
    {
        var have = _context.Select(t => t.Key).ToHashSet();
        var added = new List<int>();
        foreach (var t in tracks)
        {
            if (!have.Add(t.Key)) continue;
            t.IsLiked = _liked.Contains(t);
            _context.Add(t);
            added.Add(_context.Count - 1);
        }
        if (added.Count == 0) return;

        if (Shuffle)
        {
            // Reshuffle everything still to come together with the new songs, so they're spread evenly
            // and the ones you haven't heard lately still come first.
            var upcoming = _order.Skip(_orderPos + 1).Concat(added).ToList();
            _order.RemoveRange(_orderPos + 1, _order.Count - _orderPos - 1);
            _order.AddRange(FreshShuffle(upcoming));
        }
        else
            _order.AddRange(added);

        RefreshQueue();
        _youtube.Prefetch(PeekNext());
    }

    /// <summary>Rebuilds the play order so that context index <paramref name="current"/> is at _orderPos.</summary>
    private void BuildOrder(int current)
    {
        _order = Enumerable.Range(0, _context.Count).ToList();
        if (!Shuffle)
        {
            _orderPos = current;
            return;
        }

        _order.Remove(current);
        _order = FreshShuffle(_order);
        _order.Insert(0, current);
        _orderPos = 0;
    }

    /// <summary>
    /// Shuffle that remembers what you've heard. A plain shuffle starts from scratch every time you press
    /// play, so in a big playlist some songs keep coming back while others never get their turn. Here every
    /// song gets a random draw plus a head start for how long ago it last played (from the listening
    /// history): songs never played, or not for a long time, land near the front; what you heard recently
    /// lands near the back. The order is still different every time.
    /// </summary>
    private List<int> FreshShuffle(IEnumerable<int> indices)
    {
        var draw = FreshDraw();
        return indices.OrderByDescending(i => draw(_context[i])).ToList();
    }

    /// <summary>A random draw per song for <see cref="FreshShuffle"/>: sort by it, highest first.</summary>
    private Func<Track, double> FreshDraw()
    {
        // How many plays ago each song was last heard (0 = the latest play).
        var lastHeard = new Dictionary<string, int>();
        var plays = 0;
        foreach (var t in _history) lastHeard.TryAdd(t.Key, plays++);
        foreach (var e in _listening.Events)
        {
            if (e.Seconds < 10) continue; // skipped straight away: not really heard
            lastHeard.TryAdd(e.Key, plays++);
        }
        var span = Math.Max(1, plays);

        // Each song is drawn once, however often the sort asks.
        var draws = new Dictionary<Track, double>(ReferenceEqualityComparer.Instance);
        return t =>
        {
            if (draws.TryGetValue(t, out var d)) return d;
            // 0 = just heard ... 1 = never heard (or longer ago than the history goes back).
            var age = lastHeard.TryGetValue(t.Key, out var ago) ? (double)ago / span : 1.0;
            return draws[t] = age + Random.Shared.NextDouble() * 0.6;
        };
    }
    private async Task PlayTrackAsync(Track track)
    {
        _playCts?.Cancel();
        var cts = _playCts = new CancellationTokenSource();

        RecordListen();
        _player.Stop();
        if (CurrentTrack != null && !ReferenceEquals(CurrentTrack, track))
        {
            _history.Remove(CurrentTrack);
            _history.Insert(0, CurrentTrack);
            if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
        }
        CurrentTrack = track;
        RefreshQueue();
        IsPlaying = false;
        IsResolving = true;
        PositionSeconds = 0;
        PositionText = "0:00";
        DurationSeconds = Math.Max(1, track.Duration.TotalSeconds);
        DurationText = track.DurationText;
        ClearLyrics(); // loaded once the audio is ready, matched to its exact length

        try
        {
            var path = await _youtube.GetAudioFileAsync(track, cts.Token);
            if (cts.IsCancellationRequested) return;

            _player.Play(path);
            _listenTrack = track;
            if (ReferenceEquals(track, _resumeTrack) && _resumeAt > TimeSpan.FromSeconds(1))
                _player.Seek(_resumeAt); // continuing the restored session
            _resumeTrack = null;
            _resumeAt = TimeSpan.Zero;
            _ = LoadLyricsAsync(track, _player.Duration, path);
            _failures = 0;
            IsPlaying = true;
            DurationSeconds = Math.Max(1, _player.Duration.TotalSeconds);
            DurationText = Track.Format(_player.Duration);
            StatusText = "";
            _youtube.NowPlaying(track, PeekNext());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            await SkipBrokenTrackAsync(ex.Message);
        }
        finally
        {
            if (_playCts == cts) IsResolving = false;
        }
    }

    /// <summary>
    /// Skips an unplayable track. Gives up after 5 in a row (e.g. offline) instead of racing through the playlist.
    /// </summary>
    private async Task SkipBrokenTrackAsync(string reason)
    {
        var cts = _playCts;
        var name = string.IsNullOrWhiteSpace(CurrentTrack?.Title) ? "a song" : $"“{CurrentTrack.Title}”";
        StatusText = $"Couldn't play {name}: {reason.TrimEnd('.')}. Skipping.";
        if (++_failures >= 5)
        {
            _player.Stop();
            IsPlaying = false;
            StatusText = "Several songs in a row couldn't play. Check your internet connection, then press play.";
            _failures = 0;
            return;
        }
        await Task.Delay(800);
        if (cts == _playCts) await PlayNextAsync(manual: false);
    }

    private Track? PeekNext()
    {
        if (_upNext.Count > 0) return _upNext[0];
        if (_order.Count == 0) return null;
        if (_orderPos + 1 < _order.Count) return _context[_order[_orderPos + 1]];
        return Repeat == RepeatMode.All ? _context[_order[0]] : null;
    }

    private void OnTrackEnded()
    {
        // A file that stops long before its length didn't really play; don't treat it as a normal end.
        if (_player.Duration - _player.Position > TimeSpan.FromSeconds(5))
        {
            _ = SkipBrokenTrackAsync("the audio stopped early");
            return;
        }
        RecordListen();
        if (CurrentTrack != null) _youtube.Finished(CurrentTrack);
        if (Repeat == RepeatMode.One && CurrentTrack != null)
            _ = PlayTrackAsync(CurrentTrack);
        else
            _ = PlayNextAsync(manual: false);
    }

    [RelayCommand]
    private Task NextAsync() => PlayNextAsync(manual: true);

    private Task PlayNextAsync(bool manual)
    {
        if (_upNext.Count > 0)
        {
            var queued = _upNext[0];
            _upNext.RemoveAt(0);
            return PlayTrackAsync(queued);
        }
        if (_order.Count == 0) return Task.CompletedTask;

        if (_orderPos + 1 < _order.Count)
            _orderPos++;
        else if (Repeat == RepeatMode.All || manual)
            _orderPos = 0;
        else if (ExtendWithRadio())
            _orderPos++;
        else
        {
            // End of the list and nothing recommended to carry on with.
            _player.Stop();
            IsPlaying = false;
            PositionSeconds = 0;
            return Task.CompletedTask;
        }
        return PlayTrackAsync(_context[_order[_orderPos]]);
    }

    private bool _radioExhausted;

    /// <summary>
    /// The playlist finished and repeat is off: keep the music going with recommended songs (autoplay),
    /// drawn from Home's picks. Appends a shuffled batch of fresh songs to the end of the play order.
    /// Returns false when there's nothing new to recommend, so playback stops cleanly.
    /// </summary>
    private bool ExtendWithRadio()
    {
        if (_radioExhausted) return false;

        var seen = _context.Select(t => t.Key).Concat(_history.Select(t => t.Key)).ToHashSet();
        var pool = RecommendedPool().Where(t => seen.Add(t.Key)).ToList();
        if (pool.Count == 0) { _radioExhausted = true; return false; }

        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        foreach (var t in pool.Take(QueuePageSize))
        {
            t.IsLiked = _liked.Contains(t);
            _context.Add(t);
            _order.Add(_context.Count - 1);
        }
        StatusText = "Playlist finished — playing songs picked for you.";
        return true;
    }

    /// <summary>Recommended songs to draw autoplay radio from: everything Home has surfaced for you.</summary>
    private List<Track> RecommendedPool() =>
        HomeSections.SelectMany(s => s.Items)
                    .SelectMany(i => i.Tracks ?? Enumerable.Empty<Track>())
                    .ToList();

    [RelayCommand]
    private Task PreviousAsync()
    {
        if (_player.Position.TotalSeconds > 3 || _order.Count == 0)
        {
            _player.Seek(TimeSpan.Zero);
            return Task.CompletedTask;
        }
        _orderPos = _orderPos > 0 ? _orderPos - 1 : _order.Count - 1;
        return PlayTrackAsync(_context[_order[_orderPos]]);
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!_player.HasTrack)
        {
            if (CurrentTrack != null) _ = PlayTrackAsync(CurrentTrack);
            else if (Tracks.Count > 0) PlayAll();
            return;
        }
        if (_player.IsPlaying) _player.Pause(); else _player.Resume();
        IsPlaying = _player.IsPlaying;
        ScheduleSessionSave(); // remember where we paused
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        Shuffle = !Shuffle;
        if (_order.Count == 0) return;
        BuildOrder(_order[_orderPos]);
        _youtube.Prefetch(PeekNext());
        RefreshQueue();
    }

    [RelayCommand]
    private void CycleRepeat()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        ScheduleSessionSave();
    }

    public void SeekTo(double seconds)
    {
        _player.Seek(TimeSpan.FromSeconds(seconds));
        PositionSeconds = seconds;
        PositionText = Track.Format(TimeSpan.FromSeconds(seconds));
        Seeked?.Invoke();
    }

    public void SeekToLyric(LyricLine line)
    {
        if (line.Time is { } t) SeekTo(t.TotalSeconds);
    }

    private void Tick()
    {
        if (!_player.HasTrack) return;
        IsPlaying = _player.IsPlaying;
        var pos = _player.Position;
        if (!IsSeeking)
        {
            PositionSeconds = pos.TotalSeconds;
            PositionText = Track.Format(pos);
        }

        var idx = -1;
        for (var i = 0; i < Lyrics.Count; i++)
        {
            if (Lyrics[i].Time is { } t && t <= pos + TimeSpan.FromMilliseconds(250)) idx = i;
            else if (Lyrics[i].Time != null) break;
        }
        if (idx != CurrentLyricIndex)
        {
            CurrentLyric = idx >= 0 ? Lyrics[idx] : null;
            CurrentLyricIndex = idx;
        }
    }

    private void ClearLyrics()
    {
        _lyricsCts?.Cancel();
        Lyrics.Clear();
        CurrentLyric = null;
        CurrentLyricIndex = -1;
        LyricsStatus = "Loading lyrics…";
    }

    private async Task LoadLyricsAsync(Track track, TimeSpan audioDuration, string audioPath)
    {
        _lyricsCts?.Cancel();
        var cts = _lyricsCts = new CancellationTokenSource();
        Lyrics.Clear();
        CurrentLyric = null;
        CurrentLyricIndex = -1;
        LyricsStatus = "Loading lyrics…";
        try
        {
            var lines = await _lyrics.GetAsync(track, audioDuration, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var l in lines) Lyrics.Add(l);
            LyricsStatus = lines.Count == 0 ? "No lyrics found for this song." : "";
        }
        catch (Exception) when (!cts.IsCancellationRequested)
        {
            LyricsStatus = "Couldn't load lyrics.";
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _sessionTimer?.Stop();
        RecordListen();
        SaveSession();
        SaveSettings();
        _timer.Stop();
        _player.Dispose();
        Storage.Flush(); // session and listening history are written in the background
    }
}
