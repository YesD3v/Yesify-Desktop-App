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
        if (HomeSections.Count == 0) HomeStatus = "Finding music for you…";
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
            if (_homeCts == cts) IsHomeBuilding = false;
        }
    }

    private void ShowHomeData(HomeData home)
    {
        _homeBuiltUtc = home.BuiltUtc;
        HomeSections.Clear();
        foreach (var s in home.Sections) HomeSections.Add(s);
        HomeStatus = home.Sections.Count > 0 ? ""
            : IsHomeBuilding ? "Finding music for you…"
            : "Play or like a few songs (or import a playlist) and Home fills up with mixes and songs picked for you.";
    }

    [RelayCommand]
    private Task OpenHomeItem(HomeItem? item)
    {
        switch (item?.Kind)
        {
            case HomeItemKind.Mix when item.Tracks != null:
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
                return OpenArtistAsync(item.Id);
            case HomeItemKind.Track when item.Tracks != null:
                StartContext(item.Tracks.ToList(), item.Index);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>An artist's popular songs, with who fans also like underneath.</summary>
    private Task OpenArtistAsync(string id)
    {
        SelectedPlaylist = null;
        return LoadViewAsync(async ct =>
        {
            var page = await _artists.GetArtistAsync(id, ct) ?? throw new InvalidOperationException("Couldn't load that artist from Spotify.");
            _artists.Save();
            if (page.Related.Count > 0) ViewDescription = "Fans also like " + string.Join(", ", page.Related.Take(6).Select(r => r.Name));
            var listeners = page.MonthlyListeners > 0 ? $"{page.MonthlyListeners:N0} monthly listeners · " : "";
            return (page.Name, $"{listeners}Popular songs", page.ImageUrl, page.TopTracks.Select(t => t.ToTrack()).ToList());
        });
    }

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
