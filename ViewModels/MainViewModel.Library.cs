using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yesify.Models;
using Yesify.Services;

namespace Yesify.ViewModels;

public enum ViewMode { Home, Library, Search, Collection, Settings }

/// <summary>Library, search, and the user's own playlists.</summary>
public sealed partial class MainViewModel
{
    private readonly LibraryService _library = new();

    // ================================================================ views

    [ObservableProperty] private ViewMode _currentView = ViewMode.Home;
    [ObservableProperty] private UserPlaylist? _currentUserPlaylist;
    [ObservableProperty] private string _viewDescription = "";

    public bool IsLibraryView => CurrentView == ViewMode.Library;
    public bool IsSettingsView => CurrentView == ViewMode.Settings;
    public bool IsSearchView => CurrentView == ViewMode.Search;
    public bool IsTrackView => CurrentView is ViewMode.Search or ViewMode.Collection;

    /// <summary>The "…" menu on the header: anything with songs that can go into a playlist.</summary>
    public bool HasHeaderMenu => CurrentView == ViewMode.Collection && (CurrentCollection != null || CurrentUserPlaylist != null || ViewIsLikedHeader);

    /// <summary>Glass placeholders while a page (artist, album, playlist, Explore...) loads.</summary>
    public bool ShowPageSkeleton => IsLoading && CurrentView == ViewMode.Collection;

    /// <summary>The song list, except while the skeleton stands in for it.</summary>
    public bool ShowTrackList => IsTrackView && !ShowPageSkeleton;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPageSkeleton));
        OnPropertyChanged(nameof(ShowTrackList));
    }

    partial void OnCurrentViewChanged(ViewMode value)
    {
        OnPropertyChanged(nameof(ShowPageSkeleton));
        OnPropertyChanged(nameof(ShowTrackList));
        if (value != ViewMode.Search) StopOnlineSearch(clear: true);
        // The song page stays as it was behind Home, Library and Settings (the next page that opens resets
        // it behind its skeleton): clearing it here rebuilt the hidden page and stalled every switch ~100 ms.
        if (value is ViewMode.Home or ViewMode.Library or ViewMode.Settings) IsExploreView = false;
        OnPropertyChanged(nameof(ShowSearchOnlineSection));
        OnPropertyChanged(nameof(IsHomeView));
        OnPropertyChanged(nameof(IsLibraryView));
        OnPropertyChanged(nameof(IsSettingsView));
        OnPropertyChanged(nameof(IsSearchView));
        OnPropertyChanged(nameof(IsTrackView));
        OnPropertyChanged(nameof(HasHeaderMenu));
    }

    partial void OnCurrentUserPlaylistChanged(UserPlaylist? value) => OnPropertyChanged(nameof(HasHeaderMenu));
    partial void OnViewIsLikedHeaderChanged(bool value) => OnPropertyChanged(nameof(HasHeaderMenu));

    // ================================================================ sidebar

    public bool SidebarCollapsed
    {
        get => _settings.SidebarCollapsed;
        set
        {
            if (_settings.SidebarCollapsed == value) return;
            _settings.SidebarCollapsed = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    // ================================================================ library

    public BulkObservableCollection<LibraryItem> LibraryItems { get; } = new();

    /// <summary>
    /// Liked Songs first, then your playlists, then saved ones, narrowed by the filter chip (All / Playlists /
    /// Albums) and the Library search field. "Create playlist" and "Import" are toolbar buttons, not tiles.
    /// </summary>
    private void RefreshLibrary()
    {
        var items = new List<LibraryItem>();
        var q = (LibraryFilterQuery ?? "").Trim();
        bool Match(string title, string sub) =>
            q.Length == 0 || title.Contains(q, StringComparison.OrdinalIgnoreCase) || sub.Contains(q, StringComparison.OrdinalIgnoreCase);
        var playlists = LibraryFilter is not "Albums";
        var albums = LibraryFilter is not "Playlists";

        if (playlists && Match("Liked Songs", ""))
            items.Add(new LibraryItem(LibraryItemKind.Liked, "Liked Songs", $"{_liked.Tracks.Count} songs", null));
        if (playlists)
            foreach (var p in _library.Playlists)
                if (PlaylistSubtitle(p) is var sub && Match(p.Name, sub))
                    items.Add(new LibraryItem(LibraryItemKind.UserPlaylist, p.Name, sub, p.DisplayCover, p.Id));
        foreach (var s in _settings.SavedCollections)
        {
            var isAlbum = s.Kind is CollectionKind.Album or CollectionKind.MusicAlbum;
            if (isAlbum ? !albums : !playlists) continue;
            var sub = isAlbum ? $"Album · {s.Owner}" : s.Owner;
            if (Match(s.Name, sub)) items.Add(new LibraryItem(LibraryItemKind.Saved, s.Name, sub, s.ImageUrl, s.Id, s));
        }
        // Albums your saved songs come from (liked, your playlists, saved playlists), most-saved first.
        if (albums)
            foreach (var a in LibraryAlbums())
                if (Match(a.Title, a.Subtitle)) items.Add(a);
        RefreshLibrarySongs(q);
        LibraryEmptyText = items.Count + LibrarySongs.Count > 0 ? "" : q.Length > 0 ? $"Nothing in your library matches “{q}”." : "Nothing here yet.";
        // Pinned first (in the order they were pinned), then the rest. The page is virtualized (only the
        // rows on screen exist), so hundreds of album tiles can go in at once.
        var pins = _settings.PinnedLibrary;
        var pinned = items.Where(i => pins.Contains(i.PinKey)).OrderBy(i => pins.IndexOf(i.PinKey)).Select(i => i with { IsPinned = true });
        var next = pinned.Concat(items.Where(i => !pins.Contains(i.PinKey))).ToList();
        // Opening Library again with nothing changed keeps the tiles that are there: replacing them all
        // rebuilt every row on screen (a ~100 ms hitch on each visit).
        if (next.Count == LibraryItems.Count && next.Select(TileKey).SequenceEqual(LibraryItems.Select(TileKey))) return;
        LibraryItems.ReplaceAll(next);
    }

    /// <summary>Everything a Library tile shows (and what opening it uses), to tell whether it changed.</summary>
    private static (LibraryItemKind, string, string, string, string?, bool, int, int) TileKey(LibraryItem i) =>
        (i.Kind, i.PinKey, i.Title, i.Subtitle, i.Image, i.IsPinned, i.AlbumSongs?.Count ?? -1, i.AlbumSongs?.Count(t => !t.IsDimmed) ?? -1);

    public bool IsPinned(LibraryItem item) => _settings.PinnedLibrary.Contains(item.PinKey);

    /// <summary>Pins a Library tile to the top (or unpins it).</summary>
    public void TogglePin(LibraryItem item)
    {
        if (!_settings.PinnedLibrary.Remove(item.PinKey)) _settings.PinnedLibrary.Add(item.PinKey);
        SaveSettings();
        RefreshLibrary();
    }

    [ObservableProperty] private string _libraryEmptyText = "";

    [RelayCommand]
    private void ShowLibrary()
    {
        Visit("library", () => { ShowLibrary(); return Task.CompletedTask; });
        _viewCts?.Cancel();
        IsLoading = false;
        CurrentCollection = null;
        CurrentUserPlaylist = null;
        ViewImageUrl = null;
        CurrentView = ViewMode.Library;
        RefreshLibrary();
    }

    [RelayCommand]
    private Task OpenLibraryItem(LibraryItem? item)
    {
        switch (item?.Kind)
        {
            case LibraryItemKind.Liked: return OpenLikedAsync();
            case LibraryItemKind.UserPlaylist when _library.Find(item.Id!) is { } p: return OpenUserPlaylistAsync(p);
            case LibraryItemKind.Saved when item.Saved != null: return OpenPlaylistAsync(item.Saved);
            case LibraryItemKind.NewPlaylist: BeginNewPlaylist(); break;
            case LibraryItemKind.Import: BeginImport(); break;
            case LibraryItemKind.Album: return OpenLibraryAlbumAsync(item);
        }
        return Task.CompletedTask;
    }

    /// <summary>Remembers the songs of an imported playlist that's in the library, for search.</summary>
    private void RememberImportedTracks()
    {
        if (CurrentCollection is { } c && IsCurrentSaved && Tracks.Count > 0) _library.CacheImported(c.Id, Tracks);
    }

    // ================================================================ search

    [ObservableProperty] private string _libraryQuery = "";
    private DispatcherTimer? _searchTimer;
    private DispatcherTimer? _onlineTimer;
    private CancellationTokenSource? _onlineCts;

    /// <summary>Search results beyond your library: songs Yesify already fetched for you, then YouTube Music.</summary>
    public ObservableCollection<Track> SearchOnline { get; } = [];

    [ObservableProperty] private bool _isSearchingOnline;
    [ObservableProperty] private string _searchOnlineStatus = "";

    /// <summary>The "More songs" divider under your library's results.</summary>
    public bool ShowSearchOnlineSection =>
        IsSearchView && (IsSearchingOnline || SearchOnline.Count > 0 || SearchOnlineStatus.Length > 0);

    partial void OnIsSearchingOnlineChanged(bool value) => OnPropertyChanged(nameof(ShowSearchOnlineSection));
    partial void OnSearchOnlineStatusChanged(string value) => OnPropertyChanged(nameof(ShowSearchOnlineSection));

    [RelayCommand]
    private void ShowSearch()
    {
        Visit("search", () => { ShowSearch(); return Task.CompletedTask; });
        _viewCts?.Cancel();
        IsLoading = false;
        CurrentCollection = null;
        CurrentUserPlaylist = null;
        ViewIsLikedHeader = false;
        ViewImageUrl = null;
        ViewTitle = ""; // the search bar above the list has the title
        ViewDescription = "";
        CurrentView = ViewMode.Search;
        RunSearch();
    }

    partial void OnLibraryQueryChanged(string value)
    {
        // Search as you type, once typing pauses for a moment.
        _searchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background,
            (_, _) => { _searchTimer!.Stop(); RunSearch(); }, Dispatcher.CurrentDispatcher);
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private static string[] QueryWords(string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();

    private static bool MatchesAll(Track t, string[] words)
    {
        var hay = $"{t.Title} {t.Artist} {t.Album}".ToLowerInvariant();
        return words.All(hay.Contains);
    }

    /// <summary>The same song from different places (Spotify, YouTube Music) compares equal.</summary>
    private static string SongIdentity(Track t)
    {
        static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return Squash(t.Title) + "|" + Squash(t.PrimaryArtist);
    }

    /// <summary>
    /// Songs in your library whose title, artist or album contain every word typed, right away; then, once
    /// typing settles, everything else Yesify can fetch (see <see cref="SearchOnlineAsync"/>).
    /// </summary>
    private void RunSearch()
    {
        if (CurrentView != ViewMode.Search) return;
        var words = QueryWords(LibraryQuery);
        Tracks.Clear();
        StopOnlineSearch(clear: true);
        ResetViewExtras();
        if (words.Length == 0)
        {
            ViewSubtitle = "Search your library and all of YouTube Music: songs, artists, albums and playlists.";
            return;
        }

        var hits = _library.AllTracks(_liked.Tracks)
            .Where(t => MatchesAll(t, words))
            .Take(300)
            .ToList();
        for (var i = 0; i < hits.Count; i++)
        {
            hits[i].Number = i + 1;
            hits[i].IsLiked = _liked.Contains(hits[i]);
            Tracks.Add(hits[i]);
        }
        ViewSubtitle = hits.Count == 0 ? "No songs in your library match." : $"{hits.Count} song{(hits.Count == 1 ? "" : "s")} in your library";

        // Online once typing has really paused, so each keystroke doesn't hit the network.
        IsSearchingOnline = true;
        var query = LibraryQuery.Trim();
        _onlineTimer?.Stop();
        _onlineTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background,
            (_, _) => { _onlineTimer!.Stop(); _ = SearchOnlineAsync(query); }, Dispatcher.CurrentDispatcher);
        _onlineTimer.Start();
    }

    private void StopOnlineSearch(bool clear)
    {
        _onlineTimer?.Stop();
        _onlineCts?.Cancel();
        IsSearchingOnline = false;
        if (!clear) return;
        // Only when there's something to clear: even an empty Clear() makes the song list (which shows
        // these under the songs) rebuild every row, which stalled every page switch.
        if (SearchOnline.Count > 0) SearchOnline.Clear();
        SearchOnlineStatus = "";
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(ShowSearchOnlineSection));
    }

    /// <summary>
    /// Everything beyond your library that matches: songs Yesify has already fetched for you (Home's
    /// mixes, artists' popular songs) show at once, then YouTube Music's results are added under them.
    /// Songs already listed above (in your library) aren't repeated.
    /// </summary>
    private async Task SearchOnlineAsync(string query)
    {
        _onlineCts?.Cancel();
        var cts = _onlineCts = new CancellationTokenSource();
        IsSearchingOnline = true;
        SearchOnlineStatus = "";
        var words = QueryWords(query);
        var have = Tracks.Select(SongIdentity).ToHashSet();

        void Add(IEnumerable<Track> songs)
        {
            foreach (var t in songs)
            {
                if (!have.Add(SongIdentity(t))) continue;
                t.Number = SearchOnline.Count + 1;
                t.IsLiked = _liked.Contains(t);
                SearchOnline.Add(t);
            }
            OnPropertyChanged(nameof(HasTracks));
            OnPropertyChanged(nameof(ShowSearchOnlineSection));
        }

        // Artists, albums and playlists, alongside the songs.
        var pages = _music.SearchAsync(query, cts.Token);
        try
        {
            Add(RecommendedPool().Where(t => MatchesAll(t, words)).Take(10));
            List<Track> online;
            try { online = await _music.SearchSongsAsync(query, cts.Token); }
            catch (OperationCanceledException) { throw; }
            catch { online = []; }
            if (online.Count == 0) online = await _youtube.SearchAsync(query, cts.Token); // regular YouTube as a fallback
            if (cts.IsCancellationRequested) return;
            Add(online.Take(20));
            if (SearchOnline.Count == 0) SearchOnlineStatus = "Nothing else found.";
            await ShowSearchPagesAsync(query, pages, cts);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (!cts.IsCancellationRequested)
        {
            SearchOnlineStatus = SearchOnline.Count == 0 ? "Couldn't search online right now. Check your connection." : "";
        }
        finally
        {
            if (_onlineCts == cts) IsSearchingOnline = false;
        }
    }

    /// <summary>
    /// Search rows from YouTube Music. Artists go on top when one is clearly what was typed
    /// ("kanye" → Kanye West); otherwise every row goes under the songs.
    /// </summary>
    private async Task ShowSearchPagesAsync(string query, Task<MusicSearchResults> pages, CancellationTokenSource cts)
    {
        MusicSearchResults found;
        try { found = await pages; }
        catch { return; } // the songs are still there
        if (cts.IsCancellationRequested || CurrentView != ViewMode.Search) return;

        static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var q = Squash(query);
        var first = found.Artists.FirstOrDefault();
        var artistFirst = first != null && q.Length > 1 && (Squash(first.Title).StartsWith(q) || q.StartsWith(Squash(first.Title)));

        if (found.Artists.Count > 0)
            (artistFirst ? ViewTopSections : ViewSections).Add(new HomeSection { Title = "Artists", Items = found.Artists });
        if (found.Albums.Count > 0) ViewSections.Add(new HomeSection { Title = "Albums", Items = found.Albums });
        if (found.Playlists.Count > 0) ViewSections.Add(new HomeSection { Title = "Playlists", Items = found.Playlists });
        if (found.CommunityPlaylists.Count > 0)
            ViewSections.Add(new HomeSection { Title = "Community playlists", Subtitle = "Made by YouTube Music listeners", Items = found.CommunityPlaylists });
    }

    // ================================================================ your playlists

    public IReadOnlyList<UserPlaylist> UserPlaylists => _library.Playlists;

    private static string PlaylistSubtitle(UserPlaylist p)
    {
        var count = p.Tracks.Count == 1 ? "1 song" : $"{p.Tracks.Count} songs";
        return $"{(p.Author.Length > 0 ? p.Author : "Playlist")} · {count}";
    }

    private Task OpenUserPlaylistAsync(UserPlaylist p)
    {
        Visit("user:" + p.Id, () => OpenUserPlaylistAsync(p));
        return LoadUserPlaylistAsync(p);
    }

    private Task LoadUserPlaylistAsync(UserPlaylist p) => LoadViewAsync(_ =>
    {
        CurrentUserPlaylist = p;
        ViewDescription = p.Description;
        return Task.FromResult(($"{p.Name}", PlaylistSubtitle(p), p.DisplayCover, p.Tracks.ToList()));
    });

    /// <summary>Adds songs to one of your playlists (skipping ones already in it).</summary>
    public void AddToPlaylist(UserPlaylist p, IReadOnlyList<Track> tracks)
    {
        var added = _library.Add(p, tracks);
        StatusText = added switch
        {
            0 => tracks.Count == 1 ? $"Already in “{p.Name}”." : $"All of these are already in “{p.Name}”.",
            1 => $"Added to “{p.Name}”.",
            _ => $"Added {added} songs to “{p.Name}”.",
        };
        if (added > 0)
        {
            RefreshLibrary();
            if (CurrentUserPlaylist == p) _ = OpenUserPlaylistAsync(p);
        }
    }

    /// <summary>Removes a song (as shown in the open playlist) from that playlist.</summary>
    public void RemoveFromCurrentPlaylist(Track track)
    {
        if (CurrentUserPlaylist is not { } p) return;
        var index = Tracks.IndexOf(track);
        if (index < 0) return;
        _library.RemoveAt(p, index);
        Tracks.RemoveAt(index);
        Renumber();
        ViewSubtitle = PlaylistSubtitle(p);
        RefreshLibrary();
    }

    public void DeletePlaylist(UserPlaylist p)
    {
        _library.Delete(p);
        StatusText = $"Deleted “{p.Name}”.";
        if (CurrentUserPlaylist == p) ShowLibrary();
        else RefreshLibrary();
    }

    /// <summary>Everything shown in the open view (album, playlist, Liked Songs, search results).</summary>
    public IReadOnlyList<Track> ShownTracks => ViewTracks();

    /// <summary>The open view's songs; on Search, your library's matches and then the ones found online.</summary>
    private List<Track> ViewTracks() => Tracks.Concat(SearchOnline).ToList();

    /// <summary>Queues everything in the open view (album, playlist, mix, Liked Songs, search results).</summary>
    public void QueueView(bool shuffled, bool next = false) =>
        QueueAll(ViewTracks(), shuffled, CurrentView == ViewMode.Collection && ViewTitle.Length > 0 ? ViewTitle : null, next);

    /// <summary>"Copy to New Playlist": the open album/playlist with its title, description and artwork.</summary>
    public void CopyViewToNewPlaylist()
    {
        var description = CurrentUserPlaylist?.Description ?? (CurrentCollection != null ? $"From {CurrentCollection.Owner}" : "");
        BeginNewPlaylist(Tracks.ToList(), ViewTitle, description, CurrentUserPlaylist?.DisplayCover ?? ViewImageUrl,
                         CurrentUserPlaylist?.Author ?? CurrentCollection?.Owner);
    }

    /// <summary>
    /// "Edit…" on an imported playlist/album: it becomes your own playlist (same songs, title, artwork and
    /// author, all editable, songs addable/removable), replacing the imported entry in the library. It no
    /// longer follows the original from then on.
    /// </summary>
    public async Task EditImportedAsync(PlaylistInfo source)
    {
        if (CurrentCollection?.Id != source.Id || IsLoading)
        {
            StatusText = $"Loading “{source.Name}”…";
            await OpenPlaylistAsync(source);
        }
        if (CurrentCollection?.Id != source.Id) return; // didn't load (offline?) or the user moved on
        BeginNewPlaylist(Tracks.ToList(), source.Name, "", source.ImageUrl, source.Owner);
        _convertFrom = source;
        EditIsNew = false; // it already exists for the user, so "Save"
        StatusText = "";
    }

    /// <summary>Imported playlists in the library, offered as "Add to Playlist" targets.</summary>
    public IReadOnlyList<PlaylistInfo> ImportedPlaylists =>
        _settings.SavedCollections.Where(s => s.Kind is not (CollectionKind.Album or CollectionKind.MusicAlbum)).ToList();

    /// <summary>
    /// Adds songs to an imported playlist. Imported playlists mirror their source and can't be changed, so
    /// it first becomes your own playlist (same songs, title, artwork and author), like "Edit…" does.
    /// </summary>
    public async Task AddToImportedAsync(PlaylistInfo source, IReadOnlyList<Track> tracks)
    {
        var viewing = CurrentCollection?.Id == source.Id;
        List<Track>? songs = viewing && !IsLoading ? Tracks.ToList() : _library.ImportedTracks(source.Id);
        if (songs == null)
        {
            StatusText = $"Loading “{source.Name}”…";
            try { songs = await FetchCollectionTracksAsync(source); }
            catch (Exception ex)
            {
                StatusText = $"Couldn't load “{source.Name}”: {ex.Message}";
                return;
            }
        }
        if (!_settings.SavedCollections.Any(s => s.Id == source.Id)) return; // removed meanwhile

        UserPlaylist p;
        try
        {
            var cover = source.ImageUrl != null && source.ImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? source.ImageUrl : null;
            p = _library.Create(source.Name, "", source.Owner, null, cover, songs);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't save the playlist: {ex.Message}";
            return;
        }
        RemoveSaved(source);
        AddToPlaylist(p, tracks);
        StatusText += " It's now your own playlist.";
        if (viewing) _ = OpenUserPlaylistAsync(p);
    }

    /// <summary>All songs of an imported playlist, fetched without touching the open view.</summary>
    private async Task<List<Track>> FetchCollectionTracksAsync(PlaylistInfo p)
    {
        if (p.Kind == CollectionKind.MusicAlbum) return (await _music.AlbumAsync(p.Id, CancellationToken.None)).Songs;
        if (p.Kind == CollectionKind.MusicPlaylist) return (await _music.PlaylistAsync(p.Id, CancellationToken.None)).Songs;
        if (p.Kind == CollectionKind.YouTubePlaylist && YoutubeExplode.Playlists.PlaylistId.TryParse(p.Id) is { } ytId)
            return (await _youtube.GetPlaylistAsync(ytId, CancellationToken.None)).Tracks;

        var result = await _spotify.GetAsync(p.Kind == CollectionKind.Album ? "album" : "playlist", p.Id, CancellationToken.None);
        var tracks = result.Tracks.ToList();
        if (p.Kind == CollectionKind.Album) return tracks;
        try
        {
            // Past the embed page's first 100 songs, as LoadRestOfPlaylistAsync does.
            var have = tracks.Select(t => t.SpotifyId).OfType<string>().ToHashSet();
            var missing = (await _spotify.GetPlaylistUrisAsync(p.Id, CancellationToken.None))
                .Where(u => !u.StartsWith("spotify:episode:"))
                .Where(u => !(u.StartsWith("spotify:track:") && have.Contains(u["spotify:track:".Length..])))
                .ToList();
            for (var i = 0; i < missing.Count; i += 50)
                tracks.AddRange(await _spotify.GetTracksAsync(missing.Skip(i).Take(50).ToList(), p.Id, CancellationToken.None));
        }
        catch { /* keep the first page */ }
        return tracks;
    }

    // ================================================================ playlist editor

    [ObservableProperty] private bool _isEditingPlaylist;
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editAuthor = "";
    [ObservableProperty] private string? _editCoverPreview;
    [ObservableProperty] private bool _editIsNew;

    private UserPlaylist? _editTarget;
    private List<Track>? _pendingTracks;
    private string? _pickedCover;
    private string? _pendingCoverUrl;
    private PlaylistInfo? _convertFrom; // imported playlist being turned into your own

    /// <summary>Opens the editor for a new playlist, optionally pre-filled (songs to add, title, artwork).</summary>
    public void BeginNewPlaylist(List<Track>? tracks = null, string? title = null, string? description = null, string? coverUrl = null, string? author = null)
    {
        _editTarget = null;
        _convertFrom = null;
        EditAuthor = author ?? "";
        _pendingTracks = tracks;
        _pickedCover = null;
        _pendingCoverUrl = coverUrl != null && coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? coverUrl : null;
        EditTitle = title ?? "";
        EditDescription = description ?? "";
        EditCoverPreview = coverUrl ?? tracks?.FirstOrDefault(t => t.ImageUrl != null)?.ImageUrl;
        EditIsNew = true;
        IsEditingPlaylist = true;
    }

    [RelayCommand]
    private void NewPlaylist() => BeginNewPlaylist();

    public void BeginEditPlaylist(UserPlaylist p)
    {
        _editTarget = p;
        _convertFrom = null;
        EditAuthor = p.Author;
        _pendingTracks = null;
        _pickedCover = null;
        _pendingCoverUrl = null;
        EditTitle = p.Name;
        EditDescription = p.Description;
        EditCoverPreview = p.DisplayCover;
        EditIsNew = false;
        IsEditingPlaylist = true;
    }

    /// <summary>An image the user picked from disk for the cover.</summary>
    public void SetPickedCover(string path)
    {
        _pickedCover = path;
        EditCoverPreview = path;
    }

    [RelayCommand]
    private void CancelEditPlaylist() => IsEditingPlaylist = false;

    [RelayCommand]
    private void SavePlaylist()
    {
        var name = EditTitle.Trim();
        if (name.Length == 0) name = "New Playlist";
        var description = EditDescription.Trim();
        var author = EditAuthor.Trim();
        try
        {
            if (_editTarget == null)
            {
                var p = _library.Create(name, description, author, _pickedCover, _pendingCoverUrl, _pendingTracks ?? []);
                if (_convertFrom is { } source)
                {
                    // The imported entry is replaced by the user's own, editable copy.
                    RemoveSaved(source);
                    StatusText = $"“{name}” is now your own playlist.";
                    IsEditingPlaylist = false;
                    RefreshLibrary();
                    _ = OpenUserPlaylistAsync(p);
                    return;
                }
                var n = p.Tracks.Count;
                StatusText = n == 0 ? $"Created “{name}”." : $"Created “{name}” with {(n == 1 ? "1 song" : $"{n} songs")}.";
            }
            else
            {
                _library.Update(_editTarget, name, description, author, _pickedCover);
                StatusText = "Playlist updated.";
                if (CurrentUserPlaylist == _editTarget) _ = OpenUserPlaylistAsync(_editTarget);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't save the playlist: {ex.Message}";
            return;
        }
        IsEditingPlaylist = false;
        RefreshLibrary();
    }
}
