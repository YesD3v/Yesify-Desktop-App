using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yesify.Models;
using Yesify.Services;

namespace Yesify.ViewModels;

public enum ViewMode { Library, Search, Collection }

/// <summary>Library, search, and the user's own playlists.</summary>
public sealed partial class MainViewModel
{
    private readonly LibraryService _library = new();

    // ================================================================ views

    [ObservableProperty] private ViewMode _currentView = ViewMode.Library;
    [ObservableProperty] private UserPlaylist? _currentUserPlaylist;
    [ObservableProperty] private string _viewDescription = "";

    public bool IsLibraryView => CurrentView == ViewMode.Library;
    public bool IsSearchView => CurrentView == ViewMode.Search;
    public bool IsTrackView => CurrentView != ViewMode.Library;

    /// <summary>The "…" menu on the header: anything with songs that can go into a playlist.</summary>
    public bool HasHeaderMenu => CurrentView == ViewMode.Collection && (CurrentCollection != null || CurrentUserPlaylist != null || ViewIsLikedHeader);

    partial void OnCurrentViewChanged(ViewMode value)
    {
        OnPropertyChanged(nameof(IsLibraryView));
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

    public ObservableCollection<LibraryItem> LibraryItems { get; } = [];

    /// <summary>Liked Songs first, then your playlists, then imported ones, then the "new" and "import" tiles.</summary>
    private void RefreshLibrary()
    {
        LibraryItems.Clear();
        LibraryItems.Add(new LibraryItem(LibraryItemKind.Liked, "Liked Songs", $"{_liked.Tracks.Count} songs", null));
        foreach (var p in _library.Playlists)
            LibraryItems.Add(new LibraryItem(LibraryItemKind.UserPlaylist, p.Name, PlaylistSubtitle(p), p.DisplayCover, p.Id));
        foreach (var s in _settings.SavedCollections)
            LibraryItems.Add(new LibraryItem(LibraryItemKind.Saved, s.Name, s.Kind == CollectionKind.Album ? $"Album · {s.Owner}" : s.Owner, s.ImageUrl, s.Id, s));
        LibraryItems.Add(new LibraryItem(LibraryItemKind.NewPlaylist, "New Playlist", "", null));
        LibraryItems.Add(new LibraryItem(LibraryItemKind.Import, "Import Playlist", "Spotify or YouTube link", null));
    }

    [RelayCommand]
    private void ShowLibrary()
    {
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

    [RelayCommand]
    private void ShowSearch()
    {
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

    /// <summary>Songs in your library whose title, artist or album contain every word typed.</summary>
    private void RunSearch()
    {
        if (CurrentView != ViewMode.Search) return;
        var words = LibraryQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
        Tracks.Clear();
        if (words.Length == 0)
        {
            ViewSubtitle = "Search the songs in your library: Liked Songs, your playlists and imported ones.";
            return;
        }

        var hits = _library.AllTracks(_liked.Tracks)
            .Where(t =>
            {
                var hay = $"{t.Title} {t.Artist} {t.Album}".ToLowerInvariant();
                return words.All(hay.Contains);
            })
            .Take(300)
            .ToList();
        for (var i = 0; i < hits.Count; i++)
        {
            hits[i].Number = i + 1;
            hits[i].IsLiked = _liked.Contains(hits[i]);
            Tracks.Add(hits[i]);
        }
        ViewSubtitle = hits.Count == 0 ? "No songs in your library match." : $"{hits.Count} song{(hits.Count == 1 ? "" : "s")} in your library";
    }

    // ================================================================ your playlists

    public IReadOnlyList<UserPlaylist> UserPlaylists => _library.Playlists;

    private static string PlaylistSubtitle(UserPlaylist p)
    {
        var count = p.Tracks.Count == 1 ? "1 song" : $"{p.Tracks.Count} songs";
        return $"{(p.Author.Length > 0 ? p.Author : "Playlist")} · {count}";
    }

    private Task OpenUserPlaylistAsync(UserPlaylist p) => LoadViewAsync(_ =>
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
    public IReadOnlyList<Track> ShownTracks => Tracks.ToList();

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
        _settings.SavedCollections.Where(s => s.Kind != CollectionKind.Album).ToList();

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
