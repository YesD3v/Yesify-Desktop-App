using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yesify.Models;
using Yesify.Services;

namespace Yesify.ViewModels;

/// <summary>
/// YouTube Music: artist, album and playlist pages, Explore (new releases, charts, moods &amp; genres),
/// radio, "Go to artist / album", and Back through everything you opened.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly YouTubeMusicService _music = new();

    // ================================================================ extras on a song page

    /// <summary>Rows of tiles under the songs (an artist's albums, an album's other versions, search results).</summary>
    public ObservableCollection<HomeSection> ViewSections { get; } = [];

    /// <summary>Rows above the songs (on Search: the artists you're probably looking for).</summary>
    public ObservableCollection<HomeSection> ViewTopSections { get; } = [];

    /// <summary>Label over the songs when they're only part of the page ("Top songs").</summary>
    [ObservableProperty] private string _viewSongsTitle = "";

    /// <summary>An artist's full song list ("See all songs").</summary>
    [ObservableProperty] private string? _viewAllSongsId;

    /// <summary>The open artist's Mix (radio).</summary>
    [ObservableProperty] private bool _hasViewRadio;
    private (string? VideoId, string? PlaylistId) _viewRadio;

    [ObservableProperty] private bool _isExploreView;

    private void ResetViewExtras()
    {
        ViewSections.Clear();
        ViewTopSections.Clear();
        ViewSongsTitle = "";
        ViewAllSongsId = null;
        HasViewRadio = false;
        _viewRadio = default;
        IsExploreView = false;
    }

    // ================================================================ back

    private readonly List<(string Key, Func<Task> Open)> _back = [];
    private Func<Task>? _here;
    private string _hereKey = "home";
    private bool _goingBack;

    public bool CanGoBack => _back.Count > 0;

    /// <summary>Called first thing by everything that opens a page: remembers where you were, for Back.</summary>
    private void Visit(string key, Func<Task> open)
    {
        if (key == _hereKey && _here != null) { _here = open; return; } // the same page again (a refresh)
        if (!_goingBack)
        {
            _back.Add((_hereKey, _here ?? (() => { ShowHome(); return Task.CompletedTask; })));
            if (_back.Count > 50) _back.RemoveAt(0);
        }
        _here = open;
        _hereKey = key;
        OnPropertyChanged(nameof(CanGoBack));
    }

    [RelayCommand]
    private void GoBack()
    {
        if (_back.Count == 0) return;
        var (_, open) = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _goingBack = true;
        try { _ = open(); }
        finally { _goingBack = false; }
        OnPropertyChanged(nameof(CanGoBack));
    }

    // ================================================================ pages

    /// <summary>Opens a YouTube Music page as a song list with rows of tiles under it.</summary>
    private Task OpenMusicPageAsync(string key, Func<CancellationToken, Task<MusicPage>> load, bool explore = false)
    {
        var self = (Func<Task>)(() => OpenMusicPageAsync(key, load, explore));
        Visit(key, self);
        SelectedPlaylist = null;
        return LoadViewAsync(async ct =>
        {
            IsExploreView = explore;
            var page = await load(ct);
            ct.ThrowIfCancellationRequested();
            CurrentCollection = page.Collection;
            ViewDescription = page.Description;
            ViewSongsTitle = page.Songs.Count > 0 ? page.SongsTitle : "";
            ViewAllSongsId = page.AllSongsId;
            _viewRadio = (page.RadioVideoId, page.RadioPlaylistId);
            HasViewRadio = page.RadioPlaylistId != null || page.RadioVideoId != null;
            // The first rows come with the page; the rest (below the fold) are added once it has faded in,
            // one per idle moment, so building them never makes the entrance or scrolling stutter.
            var rows = page.Sections.Select(Trim).ToList();
            foreach (var s in rows.Take(2)) ViewSections.Add(s);
            if (rows.Count > 2) _ = AddRowsLaterAsync(rows.Skip(2).ToList(), ct);
            var subtitle = page.Subtitle;
            if (page.Collection != null && page.Songs.Count > 0 && !subtitle.Contains("song"))
                subtitle = string.Join(" · ", new[] { subtitle, $"{page.Songs.Count} songs" }.Where(x => x.Length > 0));
            return (page.Title, subtitle, page.ImageUrl, page.Songs);
        });
    }

    /// <summary>
    /// A one-row section only ever shows the tiles that fit across the window (six at 1280 px), but every
    /// tile in it gets built and its picture loaded. Fourteen covers even a very wide window; a "Show all"
    /// page still lists everything, and song tiles keep the whole row to play through.
    /// </summary>
    private static HomeSection Trim(HomeSection s) =>
        s.Wrap || s.Items.Count <= 14 ? s
            : new HomeSection { Title = s.Title, Subtitle = s.Subtitle, Items = s.Items.Take(14).ToList(), MoreId = s.MoreId, MoreParams = s.MoreParams };

    private async Task AddRowsLaterAsync(List<HomeSection> rows, CancellationToken ct)
    {
        try
        {
            await Task.Delay(450, ct);
            foreach (var s in rows)
            {
                if (ct.IsCancellationRequested) return;
                ViewSections.Add(s);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
    }

    public Task OpenMusicArtistAsync(string channelId) =>
        OpenMusicPageAsync("artist:" + channelId, ct => _music.ArtistAsync(channelId, ct));

    public Task OpenMusicAlbumAsync(string browseId) =>
        OpenMusicPageAsync("album:" + browseId, ct => _music.AlbumAsync(browseId, ct));

    public Task OpenMusicPlaylistAsync(string browseId, string? title = null) =>
        OpenMusicPageAsync("playlist:" + browseId, async ct =>
        {
            var page = await _music.PlaylistAsync(browseId, ct);
            if (title != null) page.Title = title;
            return page;
        });

    /// <summary>A page of rows: a genre, new releases, an artist's discography, "See all" of a row.</summary>
    public Task OpenMusicBrowseAsync(string browseId, string? @params, string title)
    {
        if (@params == null && browseId.StartsWith("VL")) return OpenMusicPlaylistAsync(browseId);
        if (@params == null && browseId.StartsWith("MPREb")) return OpenMusicAlbumAsync(browseId);
        if (@params == null && browseId.StartsWith("UC")) return OpenMusicArtistAsync(browseId);
        return OpenMusicPageAsync($"browse:{browseId}:{@params}", async ct =>
        {
            var page = await _music.BrowseAsync(browseId, @params, ct);
            if (page.Title.Length == 0) page.Title = title;
            else if (title.Length > 0 && title != page.Title) page.Subtitle = title; // "Kanye West" · "Albums"
            // A single row on its own page shows every tile.
            if (page.Sections.Count == 1 && !page.Sections[0].Wrap)
            {
                var only = page.Sections[0];
                page.Sections[0] = new HomeSection { Title = only.Title == page.Title ? "" : only.Title, Items = only.Items, Wrap = true };
            }
            return page;
        });
    }

    private MusicPage? _explore;
    private DateTime _exploreUtc;

    [RelayCommand]
    private Task ShowExplore() => OpenMusicPageAsync("explore", async ct =>
    {
        if (_explore == null || DateTime.UtcNow - _exploreUtc > TimeSpan.FromMinutes(30))
        {
            _explore = await _music.ExploreAsync(ct);
            _exploreUtc = DateTime.UtcNow;
        }
        return _explore;
    }, explore: true);

    [RelayCommand]
    private Task OpenSectionMore(HomeSection? section) =>
        section?.MoreId is { } id ? OpenMusicBrowseAsync(id, section.MoreParams, section.Title) : Task.CompletedTask;

    [RelayCommand]
    private Task SeeAllSongs() =>
        ViewAllSongsId is { } id ? OpenMusicPlaylistAsync(id, $"{ViewTitle}: all songs") : Task.CompletedTask;

    // ================================================================ artist / album of a song

    [RelayCommand]
    private async Task GoToArtist(Track? track)
    {
        if (track == null) return;
        if (track.ArtistId != null)
        {
            await OpenMusicArtistAsync(track.ArtistId);
            return;
        }
        StatusText = $"Finding {track.PrimaryArtist}…";
        try
        {
            if (await _music.FindArtistAsync(track.PrimaryArtist, CancellationToken.None) is { Id: { } id })
            {
                StatusText = "";
                await OpenMusicArtistAsync(id);
            }
            else StatusText = $"Couldn't find {track.PrimaryArtist} on YouTube Music.";
        }
        catch (Exception ex) { StatusText = $"Couldn't open the artist: {ex.Message}"; }
    }

    public static bool HasAlbum(Track t) => t.AlbumId != null || (t.Album.Length > 0 && t.Album != "YouTube");

    [RelayCommand]
    private async Task GoToAlbum(Track? track)
    {
        if (track == null || !HasAlbum(track)) return;
        if (track.AlbumId != null)
        {
            await OpenMusicAlbumAsync(track.AlbumId);
            return;
        }
        StatusText = $"Finding “{track.Album}”…";
        try
        {
            if (await _music.FindAlbumAsync(track.Album, track.PrimaryArtist, CancellationToken.None) is { Id: { } id })
            {
                StatusText = "";
                await OpenMusicAlbumAsync(id);
            }
            else StatusText = $"Couldn't find “{track.Album}” on YouTube Music.";
        }
        catch (Exception ex) { StatusText = $"Couldn't open the album: {ex.Message}"; }
    }

    // ================================================================ radio

    /// <summary>Plays an endless radio of songs like this one (YouTube Music's own "Up next").</summary>
    [RelayCommand]
    private async Task StartRadio(Track? track)
    {
        if (track == null) return;
        StatusText = $"Starting radio from “{track.Title}”…";
        try
        {
            var videoId = await _youtube.VideoIdAsync(track, CancellationToken.None);
            var songs = await _music.RadioAsync(videoId, null, CancellationToken.None);
            // Keep the very same song object first, so it keeps its like and lyrics.
            songs.RemoveAll(t => t.YouTubeId == videoId);
            songs.Insert(0, track);
            PlayRadio(songs, $"Radio based on “{track.Title}”.");
        }
        catch (Exception ex) { StatusText = $"Couldn't start the radio: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task StartViewRadio()
    {
        if (!HasViewRadio) return;
        var name = ViewTitle;
        StatusText = $"Starting {name} Mix…";
        try { PlayRadio(await _music.RadioAsync(_viewRadio.VideoId, _viewRadio.PlaylistId, CancellationToken.None), $"Playing {name} Mix."); }
        catch (Exception ex) { StatusText = $"Couldn't start the Mix: {ex.Message}"; }
    }

    /// <summary>An artist's Mix straight from a tile.</summary>
    public async Task StartArtistRadioAsync(string channelId, string name)
    {
        StatusText = $"Starting {name} Mix…";
        try
        {
            var page = await _music.ArtistAsync(channelId, CancellationToken.None);
            if (page.RadioPlaylistId == null && page.RadioVideoId == null && page.Songs.Count > 0)
            {
                await StartRadio(page.Songs[0]);
                return;
            }
            PlayRadio(await _music.RadioAsync(page.RadioVideoId, page.RadioPlaylistId, CancellationToken.None), $"Playing {name} Mix.");
        }
        catch (Exception ex) { StatusText = $"Couldn't start the Mix: {ex.Message}"; }
    }

    private void PlayRadio(List<Track> songs, string status)
    {
        if (songs.Count == 0)
        {
            StatusText = "YouTube Music didn't suggest anything for that.";
            return;
        }
        foreach (var t in songs) t.IsLiked = _liked.Contains(t);
        Shuffle = false;
        StartContext(songs, 0);
        StatusText = status;
    }

    // ================================================================ songs behind a tile

    /// <summary>All of an artist's songs (their "Songs" playlist), or their top songs if that's unavailable.</summary>
    private async Task<List<Track>> ArtistSongsAsync(string channelId, CancellationToken ct)
    {
        var page = await _music.ArtistAsync(channelId, ct);
        if (page.AllSongsId != null)
        {
            try
            {
                var all = await _music.PlaylistAsync(page.AllSongsId, ct, max: 500);
                if (all.Songs.Count > 0) return all.Songs;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* top songs below */ }
        }
        return page.Songs;
    }

    private async Task<List<Track>?> MusicItemTracksAsync(HomeItem item)
    {
        if (item.Id is not { } id) return null;
        return item.Kind switch
        {
            HomeItemKind.MusicAlbum => (await _music.AlbumAsync(id, CancellationToken.None)).Songs,
            HomeItemKind.MusicPlaylist => (await _music.PlaylistAsync(id, CancellationToken.None)).Songs,
            HomeItemKind.MusicArtist => await ArtistSongsAsync(id, CancellationToken.None),
            _ => null,
        };
    }

    /// <summary>"Play All Songs" etc. on an artist page: everything they've released, not just the top five.</summary>
    public async Task<List<Track>?> ViewAllSongsAsync()
    {
        if (ViewAllSongsId is not { } id) return ShownTracks.ToList();
        StatusText = $"Loading all of {ViewTitle}'s songs…";
        try
        {
            var songs = (await _music.PlaylistAsync(id, CancellationToken.None, max: 500)).Songs;
            StatusText = "";
            foreach (var t in songs) t.IsLiked = _liked.Contains(t);
            return songs;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't load the songs: {ex.Message}";
            return null;
        }
    }

    /// <summary>On an artist page, Play starts with the top songs at once and carries on through all their songs.</summary>
    private void ContinueWithAllSongs()
    {
        if (ViewAllSongsId is not { } id) return;
        _contextFillCts?.Cancel();
        var cts = _contextFillCts = new CancellationTokenSource();
        var startedFrom = _context;
        _ = Task.Run(async () =>
        {
            try
            {
                var all = (await _music.PlaylistAsync(id, cts.Token, max: 500)).Songs;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested && ReferenceEquals(_context, startedFrom)) AppendToContext(all);
                });
            }
            catch { /* the top songs still play */ }
        });
    }

    // ================================================================ library

    /// <summary>Saves a YouTube Music album or playlist tile to the library without opening it.</summary>
    public void SaveMusicItem(HomeItem item)
    {
        if (item.Id is not { } id || item.Kind is not (HomeItemKind.MusicAlbum or HomeItemKind.MusicPlaylist)) return;
        if (_settings.SavedCollections.Any(s => s.Id == id))
        {
            StatusText = $"“{item.Title}” is already in your library.";
            return;
        }
        var parts = item.Subtitle.Split(" · ");
        _settings.SavedCollections.Insert(0, new PlaylistInfo
        {
            Id = id,
            Name = item.Title,
            Owner = parts.Length > 1 ? parts[1] : parts[0],
            ImageUrl = item.ImageUrl,
            Kind = item.Kind == HomeItemKind.MusicAlbum ? CollectionKind.MusicAlbum : CollectionKind.MusicPlaylist,
        });
        SaveSettings();
        RefreshSidebar();
        StatusText = $"Added “{item.Title}” to your library.";
    }

    public bool IsSaved(HomeItem item) => item.Id != null && _settings.SavedCollections.Any(s => s.Id == item.Id);
}
