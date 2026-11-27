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
    private readonly NotInterestedService _notInterested = new();
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
        OnStatsChanged();
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
            var not = _notInterested.Snapshot();
            var home = await Task.Run(() => _recommender.BuildAsync(history, liked, library, seed, cts.Token, not), cts.Token);

            // Popular songs past an artist's top five come without covers; fill them in before showing.
            var songs = home.Sections.SelectMany(s => s.Items).SelectMany(i => i.Tracks ?? []).Distinct().ToList();
            await _covers.FillAsync(songs, cts.Token);
            foreach (var item in home.Sections.SelectMany(s => s.Items))
            {
                if (item.Kind == HomeItemKind.Track && item.Tracks != null) item.ImageUrl = item.Tracks[item.Index].ImageUrl;
                else if (item.ImageUrl == null) item.ImageUrl = item.Tracks?.Select(t => t.ImageUrl).FirstOrDefault(u => u != null);
            }

            await AddCommunityPlaylistsAsync(home, cts.Token);

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

    /// <summary>
    /// "Community playlists for you": playlists YouTube Music listeners made around your top artists, a few
    /// per artist, after the songs picked for you. Left out quietly if YouTube Music doesn't answer.
    /// </summary>
    private async Task AddCommunityPlaylistsAsync(HomeData home, CancellationToken ct)
    {
        var artists = home.Sections.FirstOrDefault(s => s.Title == "Your top artists")?.Items.Select(i => i.Title).Take(4).ToList()
                      ?? _liked.Tracks.Select(t => t.PrimaryArtist).Where(a => a.Length > 0).GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
                          .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(4).ToList();
        if (artists.Count == 0) return;
        try
        {
            var found = await Task.WhenAll(artists.Select(a => _music.CommunityPlaylistsAsync(a, ct, 6)));
            var not = _notInterested.Snapshot();
            var picks = new List<HomeItem>();
            for (var round = 0; round < 6 && picks.Count < 14; round++) // one from each artist in turn
                foreach (var list in found)
                    if (round < list.Count && picks.All(p => p.Id != list[round].Id) && !not.Items.Contains(NotInterestedService.ItemKey(list[round])))
                        picks.Add(list[round]);
            if (picks.Count < 4) return;
            var section = new HomeSection
            {
                Title = "Community playlists for you",
                Subtitle = "Made by fans of " + string.Join(", ", artists.Take(3)),
                Items = picks.Take(14).ToList(),
            };
            var after = home.Sections.FindIndex(s => s.Title == "Songs you might like");
            home.Sections.Insert(after >= 0 ? after + 1 : Math.Min(1, home.Sections.Count), section);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Home without it */ }
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
        if (_player.HasTrack)
        {
            _listening.Record(track, _player.Position, _player.Duration);
            OnStatsChanged();
        }
    }

    // ---------------------------------------------------------------- not interested

    public bool HasNotInterested => _notInterested.Count > 0;

    public string NotInterestedSummary => _notInterested.Count == 0
        ? "Right-click a song, mix, album or artist and pick Not Interested to keep it out of Home's picks"
        : $"{_notInterested.Count:N0} {(_notInterested.Count == 1 ? "thing" : "things")} kept out of Home's picks (Discover Mix, Daily Mixes, songs and artists you might like)";

    public bool IsNotInterested(Track t) => _notInterested.Has(t);

    /// <summary>
    /// "Not Interested" on a song: it's left out of Home's recommendations from now on, and counts a little
    /// against its artist. It disappears from the picks on Home right away.
    /// </summary>
    public void NotInterested(Track track)
    {
        _notInterested.Add(track);
        var key = NotInterestedService.TitleKey(track.Title, track.Artist);
        bool Matches(Track t) => t.Key == track.Key || NotInterestedService.TitleKey(t.Title, t.Artist) == key;
        DropFromHome(item => item.Kind == HomeItemKind.Track && item.Tracks != null && item.Index < item.Tracks.Count && Matches(item.Tracks[item.Index]),
                     mixes: tracks => tracks.RemoveAll(Matches) > 0);
        StatusText = $"Got it. “{track.Title}” won't be recommended to you again.";
        OnNotInterestedChanged();
    }

    /// <summary>"Not Interested" on a Home tile (a mix, album, playlist or artist): it's taken off Home for good.</summary>
    public void NotInterested(HomeItem item)
    {
        _notInterested.Add(item);
        var artist = item.Kind is HomeItemKind.Artist or HomeItemKind.MusicArtist ? item.Title : null;
        bool ByArtist(Track t) => artist != null && t.Artist.Split(',')[0].Trim().Equals(artist, StringComparison.OrdinalIgnoreCase);
        var key = NotInterestedService.ItemKey(item);
        DropFromHome(i => ReferenceEquals(i, item) || NotInterestedService.ItemKey(i) == key
                          || i.Kind == HomeItemKind.Track && i.Tracks != null && i.Index < i.Tracks.Count && ByArtist(i.Tracks[i.Index]),
                     mixes: artist == null ? null : tracks => tracks.RemoveAll(ByArtist) > 0);
        StatusText = artist != null ? $"Got it. You'll see less of {artist} on Home." : $"Got it. “{item.Title}” won't show up on Home again.";
        OnNotInterestedChanged();
    }

    /// <summary>Takes matching tiles off Home (rebuilding only the rows that change) and saves it.</summary>
    private void DropFromHome(Func<HomeItem, bool> tile, Func<List<Track>, bool>? mixes)
    {
        for (var i = 0; i < HomeSections.Count; i++)
        {
            var section = HomeSections[i];
            if (section.Title == "Recently played") continue;
            var keep = section.Items.Where(item => !tile(item)).ToList();
            var changed = keep.Count != section.Items.Count;
            if (mixes != null)
                foreach (var mix in keep.Where(x => x.Kind == HomeItemKind.Mix && x.Tracks != null))
                    changed |= mixes(mix.Tracks!);
            if (!changed) continue;
            if (keep.Count == 0)
            {
                HomeSections.RemoveAt(i--);
                continue;
            }
            // Song tiles carry their row (so playing continues through it): point them at the new row.
            var songs = keep.Where(x => x.Kind == HomeItemKind.Track && x.Tracks != null && x.Index < x.Tracks.Count).Select(x => x.Tracks![x.Index]).ToList();
            var rebuilt = keep.Select(x => x.Kind == HomeItemKind.Track && x.Tracks != null && x.Index < x.Tracks.Count
                ? new HomeItem { Kind = x.Kind, Title = x.Title, Subtitle = x.Subtitle, ImageUrl = x.ImageUrl, Id = x.Id, Tracks = songs, Index = songs.IndexOf(x.Tracks[x.Index]) }
                : x).ToList();
            HomeSections[i] = new HomeSection { Title = section.Title, Subtitle = section.Subtitle, Items = rebuilt, MoreId = section.MoreId, MoreParams = section.MoreParams, Wrap = section.Wrap };
        }
        var data = new HomeData { BuiltUtc = _homeBuiltUtc, Sections = HomeSections.ToList() };
        try { Storage.Save(HomeFile, data); } catch { /* still off Home for now */ }
    }

    private void OnNotInterestedChanged()
    {
        OnPropertyChanged(nameof(HasNotInterested));
        OnPropertyChanged(nameof(NotInterestedSummary));
    }

    [RelayCommand]
    private void ClearNotInterested()
    {
        _notInterested.Clear();
        OnNotInterestedChanged();
        StatusText = "Not Interested reset. Home can recommend everything again.";
        _ = BuildHomeAsync(DaySeed());
    }

    // ---------------------------------------------------------------- landing page stats

    public bool HasListeningStats => _listening.Stats.Songs.Count > 0;

    /// <summary>"1,284 songs"</summary>
    public string StatsSongsText => _listening.Stats.Songs.Count == 1 ? "1 song" : $"{_listening.Stats.Songs.Count:N0} songs";

    /// <summary>"86h of music" (minutes while it's under an hour).</summary>
    public string StatsHoursText
    {
        get
        {
            var h = _listening.Stats.Seconds / 3600;
            if (h < 1) return $"{Math.Max(0, (int)Math.Round(h * 60))} min of music";
            return h < 10 ? $"{h:0.#}h of music" : $"{h:N0}h of music";
        }
    }

    /// <summary>Since when, how many plays, and who you've been playing most lately.</summary>
    public string StatsDetail
    {
        get
        {
            var stats = _listening.Stats;
            if (stats.Songs.Count == 0) return "Play something and your listening adds up here.";
            var parts = new List<string> { $"Since {stats.Since.ToLocalTime():MMMM yyyy}", stats.Plays == 1 ? "1 play" : $"{stats.Plays:N0} plays" };
            var month = DateTime.UtcNow.AddDays(-30);
            var top = _listening.Events.Where(e => e.AtUtc >= month)
                .GroupBy(e => e.Artist.Split(',')[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.First().Artist.Split(',')[0].Trim(), Seconds: g.Sum(e => e.Seconds)))
                .Where(a => a.Name.Length > 0)
                .OrderByDescending(a => a.Seconds).FirstOrDefault();
            if (top.Name != null) parts.Add($"On repeat: {top.Name}");
            return string.Join("  ·  ", parts);
        }
    }

    private void OnStatsChanged()
    {
        OnPropertyChanged(nameof(HasListeningStats));
        OnPropertyChanged(nameof(StatsSongsText));
        OnPropertyChanged(nameof(StatsHoursText));
        OnPropertyChanged(nameof(StatsDetail));
    }

    /// <summary>The visualizer's levels: what's playing right now (false when nothing is).</summary>
    public bool ReadSpectrum(float[] bands) => _player.ReadSpectrum(bands);

    [RelayCommand]
    private void ClearListeningHistory()
    {
        _listening.Clear();
        StatusText = "Listening history cleared. Home starts fresh.";
        _ = BuildHomeAsync(DaySeed());
    }
}
