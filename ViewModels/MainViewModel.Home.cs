using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yesify.Models;
using Yesify.Services;

namespace Yesify.ViewModels;

/// <summary>Home: mixes, recommendations and new releases built from what you play (see <see cref="Recommender"/>).</summary>
public sealed partial class MainViewModel
{
    private const string HomeFile = "home.json";

    private readonly ListeningHistory _listening = new();
    private readonly SpotifyArtistService _artists = new();
    private Recommender? _recommender;
    private CancellationTokenSource? _homeCts;
    private Track? _listenTrack;

    public ObservableCollection<HomeSection> HomeSections { get; } = [];

    [ObservableProperty] private bool _isHomeBuilding;
    [ObservableProperty] private string _homeStatus = "";

    public bool IsHomeView => CurrentView == ViewMode.Home;

    /// <summary>Placeholder rows that shimmer while the very first Home is built.</summary>
    public bool ShowHomeSkeleton => IsHomeBuilding && HomeSections.Count == 0;

    partial void OnIsHomeBuildingChanged(bool value) => OnPropertyChanged(nameof(ShowHomeSkeleton));

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good evening",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    [RelayCommand]
    private void ShowHome()
    {
        Visit("home", () => { ShowHome(); return Task.CompletedTask; });
        _viewCts?.Cancel();
        IsLoading = false;
        CurrentCollection = null;
        CurrentUserPlaylist = null;
        ViewImageUrl = null;
        CurrentView = ViewMode.Home;
        OnPropertyChanged(nameof(Greeting));
        // A new day means new mixes.
        if (!IsHomeBuilding && (HomeSections.Count == 0 || _homeBuiltUtc.ToLocalTime().Date != DateTime.Now.Date)) _ = BuildHomeAsync(DaySeed());
    }

    private DateTime _homeBuiltUtc;

    /// <summary>Shows the last Home right away; builds a new one if it's from another day.</summary>
    private void LoadHome()
    {
        var saved = Storage.Load<HomeData>(HomeFile);
        ShowHomeData(saved);
        if (saved.Sections.Count == 0 || saved.BuiltUtc.ToLocalTime().Date != DateTime.Now.Date) _ = BuildHomeAsync(DaySeed());
    }

    [RelayCommand]
    private Task RefreshHome() => BuildHomeAsync(Random.Shared.Next());

    private static int DaySeed() => DateTime.Now.Date.GetHashCode();

    private async Task BuildHomeAsync(int seed)
    {
        _homeCts?.Cancel();
        var cts = _homeCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IsHomeBuilding = true;
        if (HomeSections.Count == 0) HomeStatus = ""; // the skeleton shows it's on its way
        try
        {
            _recommender ??= new Recommender(_artists);
            var history = _listening.Events.ToList();
            var liked = _liked.Tracks.ToList();
            var library = _library.AllTracks([]).ToList();
            var home = await Task.Run(() => _recommender.BuildAsync(history, liked, library, seed, cts.Token), cts.Token);

            // Popular songs past an artist's top five come without covers; fill them in before showing.
            var songs = home.Sections.SelectMany(s => s.Items).SelectMany(i => i.Tracks ?? []).Distinct().ToList();
            await _covers.FillAsync(songs, cts.Token);
            foreach (var item in home.Sections.SelectMany(s => s.Items))
            {
                if (item.Kind == HomeItemKind.Track && item.Tracks != null) item.ImageUrl = item.Tracks[item.Index].ImageUrl;
                else if (item.ImageUrl == null) item.ImageUrl = item.Tracks?.Select(t => t.ImageUrl).FirstOrDefault(u => u != null);
            }

            if (cts.IsCancellationRequested) return;
            ShowHomeData(home);
            try { Storage.Save(HomeFile, home); } catch { /* shown anyway */ }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (HomeSections.Count == 0) HomeStatus = $"Couldn't reach Spotify to build Home ({ex.Message}). Try again in a moment.";
        }
        finally
        {
            if (_homeCts == cts)
            {
                IsHomeBuilding = false;
                if (HomeSections.Count == 0 && HomeStatus.Length == 0)
                    HomeStatus = "Play or like a few songs (or import a playlist) and Home fills up with mixes and songs picked for you.";
            }
        }
    }

    private void ShowHomeData(HomeData home)
    {
        _homeBuiltUtc = home.BuiltUtc;
        HomeSections.Clear();
        foreach (var s in home.Sections) HomeSections.Add(s);
        OnPropertyChanged(nameof(ShowHomeSkeleton));
        HomeStatus = home.Sections.Count > 0 || IsHomeBuilding ? ""
            : "Play or like a few songs (or import a playlist) and Home fills up with mixes and songs picked for you.";
    }

    [RelayCommand]
    private Task OpenHomeItem(HomeItem? item)
    {
        switch (item?.Kind)
        {
            case HomeItemKind.Mix when item.Tracks != null:
                Visit("mix:" + item.Title, () => OpenHomeItem(item));
                SelectedPlaylist = null;
                return LoadViewAsync(_ =>
                {
                    ViewDescription = item.Description;
                    return Task.FromResult((item.Title, $"{item.Subtitle} · {item.Tracks.Count} songs", item.ImageUrl, item.Tracks.ToList()));
                });
            case HomeItemKind.Album when item.Id != null:
                SelectedPlaylist = null;
                return OpenSpotifyCollectionAsync("album", item.Id);
            case HomeItemKind.Artist when item.Id != null:
                return OpenArtistAsync(item.Id, item.Title);
            case HomeItemKind.Track when item.Tracks != null:
                StartContext(item.Tracks.ToList(), item.Index);
                break;
            case HomeItemKind.MusicAlbum when item.Id != null:
                return OpenMusicAlbumAsync(item.Id);
            case HomeItemKind.MusicArtist when item.Id != null:
                return OpenMusicArtistAsync(item.Id);
            case HomeItemKind.MusicPlaylist when item.Id != null:
                return OpenMusicPlaylistAsync(item.Id);
            case HomeItemKind.MusicCategory when item.Id != null:
                return OpenMusicBrowseAsync(item.Id, item.Params, item.Title);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The songs behind a Home tile, without opening it: the song itself, a mix's songs, an album's songs
    /// or an artist's popular songs (those two are fetched). Null if they couldn't be loaded.
    /// </summary>
    public async Task<List<Track>?> HomeItemTracksAsync(HomeItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case HomeItemKind.Track when item.Tracks != null && item.Index < item.Tracks.Count:
                    return [item.Tracks[item.Index]];
                case HomeItemKind.Mix when item.Tracks != null:
                    return item.Tracks.ToList();
                case HomeItemKind.Album when item.Id != null:
                    StatusText = $"Loading “{item.Title}”…";
                    var album = (await _spotify.GetAsync("album", item.Id, CancellationToken.None)).Tracks;
                    StatusText = "";
                    return album;
                case HomeItemKind.MusicAlbum or HomeItemKind.MusicPlaylist or HomeItemKind.MusicArtist:
                    StatusText = $"Loading “{item.Title}”…";
                    var music = await MusicItemTracksAsync(item);
                    StatusText = "";
                    return music;
                case HomeItemKind.Artist when item.Id != null:
                    StatusText = $"Loading “{item.Title}”…";
                    var page = await _artists.GetArtistAsync(item.Id, CancellationToken.None)
                               ?? throw new InvalidOperationException("Spotify didn't answer");
                    _artists.Save();
                    var songs = page.TopTracks.Select(t => t.ToTrack()).ToList();
                    await _covers.FillAsync(songs, CancellationToken.None);
                    StatusText = "";
                    return songs;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't load “{item.Title}”: {ex.Message}";
        }
        return null;
    }

    /// <summary>
    /// A Spotify artist from Home: opens their full YouTube Music page (top songs, albums, singles, videos,
    /// similar artists), or Spotify's popular songs if YouTube Music doesn't have them.
    /// </summary>
    private Task OpenArtistAsync(string id, string name) => OpenMusicPageAsync("spotify-artist:" + id, async ct =>
    {
        try
        {
            if (await _music.FindArtistAsync(name, ct) is { Id: { } channel } found &&
                new string(found.Title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()) ==
                new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()))
                return await _music.ArtistAsync(channel, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Spotify's page below */ }

        var page = await _artists.GetArtistAsync(id, ct) ?? throw new InvalidOperationException("Couldn't load that artist.");
        _artists.Save();
        var result = new MusicPage
        {
            Title = page.Name,
            Subtitle = page.MonthlyListeners > 0 ? $"{page.MonthlyListeners:N0} monthly listeners" : "Artist",
            ImageUrl = page.ImageUrl,
            SongsTitle = "Popular",
            Description = page.Related.Count > 0 ? "Fans also like " + string.Join(", ", page.Related.Take(6).Select(r => r.Name)) : "",
        };
        result.Songs.AddRange(page.TopTracks.Select(t => t.ToTrack()));
        return result;
    });

    // ---------------------------------------------------------------- listening history

    /// <summary>Saves how far the song that was playing got (a full listen or a skip) for Home.</summary>
    private void RecordListen()
    {
        if (_listenTrack is not { } track) return;
        _listenTrack = null;
        if (_player.HasTrack) _listening.Record(track, _player.Position, _player.Duration);
    }

    [RelayCommand]
    private void ClearListeningHistory()
    {
        _listening.Clear();
        StatusText = "Listening history cleared. Home starts fresh.";
        _ = BuildHomeAsync(DaySeed());
    }
}
