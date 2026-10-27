using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yesify.Models;

namespace Yesify.Services;

public sealed record ArtistRef(string Id, string Name, string? ImageUrl);

public sealed record ArtistRelease(string Id, string Name, string Type, int Year, string? ImageUrl);

public sealed record ArtistTrack(string Id, string Title, string Artist, string Album, long DurationMs, bool Explicit, string? ImageUrl)
{
    public Track ToTrack() => new()
    {
        Title = Title,
        Artist = Artist,
        Album = Album,
        Duration = TimeSpan.FromMilliseconds(DurationMs),
        SpotifyId = Id,
        IsExplicit = Explicit,
        ImageUrl = ImageUrl,
    };
}

/// <summary>What an artist's public Spotify page shows: popular songs, "Fans also like" and releases.</summary>
public sealed class ArtistPage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public long MonthlyListeners { get; init; }
    /// <summary>Most popular first.</summary>
    public List<ArtistTrack> TopTracks { get; init; } = [];
    /// <summary>"Fans also like", most similar first.</summary>
    public List<ArtistRef> Related { get; init; } = [];
    public List<ArtistRelease> Releases { get; init; } = [];
    public DateTime FetchedUtc { get; init; }
}

/// <summary>
/// Reads artists from Spotify's public web pages, no account: the artist page (open.spotify.com/artist/…)
/// carries its data as JSON for the page's own script, and the embed widget lists the top 10 songs with
/// their lengths. Pages are cached on disk for a few days; artists don't change that fast.
/// </summary>
public sealed partial class SpotifyArtistService
{
    private const string ArtistsFile = "spotify-artists.json";
    private const string TrackArtistsFile = "spotify-track-artists.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(4);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(4);
    private readonly ConcurrentDictionary<string, ArtistPage> _artists;
    private readonly ConcurrentDictionary<string, List<ArtistRef>> _trackArtists;
    private readonly object _saveLock = new();

    public SpotifyArtistService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Yesify/1.0");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        _artists = new(Storage.Load<Dictionary<string, ArtistPage>>(ArtistsFile));
        _trackArtists = new(Storage.Load<Dictionary<string, List<ArtistRef>>>(TrackArtistsFile));
    }

    [GeneratedRegex("""<script[^>]*id="initialState"[^>]*>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex InitialState();

    [GeneratedRegex("""<script id="__NEXT_DATA__" type="application/json">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextData();

    /// <summary>A cached page, even an old one, without touching the network.</summary>
    public ArtistPage? Cached(string id) => _artists.TryGetValue(id, out var page) ? page : null;

    public bool IsFresh(string id) => Cached(id) is { } page && DateTime.UtcNow - page.FetchedUtc < MaxAge;

    /// <summary>The artist's page; an old copy if Spotify can't be reached.</summary>
    public async Task<ArtistPage?> GetArtistAsync(string id, CancellationToken ct)
    {
        if (IsFresh(id)) return Cached(id);
        await _gate.WaitAsync(ct);
        try
        {
            if (IsFresh(id)) return Cached(id);
            var page = await FetchArtistAsync(id, ct);
            _artists[id] = page;
            return page;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Cached(id); }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArtistPage> FetchArtistAsync(string id, CancellationToken ct)
    {
        var html = await _http.GetStringAsync($"https://open.spotify.com/artist/{id}", ct);
        var match = InitialState().Match(html);
        if (!match.Success) throw new InvalidOperationException("Spotify's artist page changed.");
        var raw = match.Groups[1].Value.Trim();
        var json = JsonNode.Parse(raw.StartsWith('{') ? raw : Encoding.UTF8.GetString(Convert.FromBase64String(raw)));
        var a = json?["entities"]?["items"]?[$"spotify:artist:{id}"] ?? throw new InvalidOperationException("No artist on that page.");

        static string? IdOf(JsonNode? uri) => ((string?)uri)?.Split(':').LastOrDefault();

        var popular = (a["discography"]?["topTracks"]?["items"]?.AsArray() ?? [])
            .Select(i => i?["track"])
            .Where(t => IdOf(t?["uri"]) != null)
            .Select(t => new ArtistTrack(
                IdOf(t!["uri"])!,
                (string?)t["name"] ?? "",
                string.Join(", ", t["artists"]?["items"]?.AsArray().Select(x => (string?)x?["profile"]?["name"]).OfType<string>() ?? []),
                (string?)t["albumOfTrack"]?["name"] ?? "",
                0,
                (string?)t["contentRating"]?["label"] == "EXPLICIT",
                PickImage(t["albumOfTrack"]?["coverArt"]?["sources"])))
            .ToList();

        var related = (a["relatedContent"]?["relatedArtists"]?["items"]?.AsArray() ?? [])
            .Where(r => IdOf(r?["uri"]) != null)
            .Select(r => new ArtistRef(IdOf(r!["uri"])!, (string?)r["profile"]?["name"] ?? "", PickImage(r["visuals"]?["avatarImage"]?["sources"])))
            .ToList();

        var d = a["discography"];
        var releaseNodes = new[] { d?["latest"] }
            .Concat(d?["popularReleasesAlbums"]?["items"]?.AsArray() ?? [])
            .Concat((d?["albums"]?["items"]?.AsArray() ?? []).Select(x => x?["releases"]?["items"]?[0]))
            .Concat((d?["singles"]?["items"]?.AsArray() ?? []).Select(x => x?["releases"]?["items"]?[0]));
        var releases = releaseNodes
            .Where(r => IdOf(r?["uri"]) != null)
            .Select(r => new ArtistRelease(IdOf(r!["uri"])!, (string?)r["name"] ?? "", (string?)r["type"] ?? "", (int?)r["date"]?["year"] ?? 0,
                                           PickImage(r["coverArt"]?["sources"])))
            .DistinctBy(r => r.Id)
            .ToList();

        // The embed widget has the top 10 (the page only 5) and their lengths; the page has their covers.
        var top = popular;
        try
        {
            var embed = await _http.GetStringAsync($"https://open.spotify.com/embed/artist/{id}", ct);
            var data = NextData().Match(embed);
            var list = data.Success ? JsonNode.Parse(data.Groups[1].Value)?["props"]?["pageProps"]?["state"]?["data"]?["entity"]?["trackList"]?.AsArray() : null;
            if (list is { Count: > 0 })
            {
                var byId = popular.ToDictionary(t => t.Id);
                top = list
                    .Where(t => IdOf(t?["uri"]) != null)
                    .Select(t =>
                    {
                        var trackId = IdOf(t!["uri"])!;
                        byId.TryGetValue(trackId, out var p);
                        return new ArtistTrack(trackId, (string?)t["title"] ?? p?.Title ?? "",
                            ((string?)t["subtitle"] ?? p?.Artist ?? "").Replace(' ', ' '),
                            p?.Album ?? "", (long?)t["duration"] ?? 0, (bool?)t["isExplicit"] ?? p?.Explicit ?? false, p?.ImageUrl);
                    })
                    .ToList();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* keep the page's five */ }

        return new ArtistPage
        {
            Id = id,
            Name = (string?)a["profile"]?["name"] ?? "",
            ImageUrl = PickImage(a["visuals"]?["avatarImage"]?["sources"]),
            MonthlyListeners = (long?)a["stats"]?["monthlyListeners"] ?? 0,
            TopTracks = top,
            Related = related,
            Releases = releases,
            FetchedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>The credited artists of a song (with their Spotify ids), from its embed page. Cached for good.</summary>
    public async Task<List<ArtistRef>> GetTrackArtistsAsync(string trackId, CancellationToken ct)
    {
        if (_trackArtists.TryGetValue(trackId, out var known)) return known;
        await _gate.WaitAsync(ct);
        try
        {
            var html = await _http.GetStringAsync($"https://open.spotify.com/embed/track/{trackId}", ct);
            var data = NextData().Match(html);
            var artists = (data.Success ? JsonNode.Parse(data.Groups[1].Value)?["props"]?["pageProps"]?["state"]?["data"]?["entity"]?["artists"]?.AsArray() : null) ?? [];
            var result = artists
                .Select(x => (Id: ((string?)x?["uri"])?.Split(':').LastOrDefault(), Name: (string?)x?["name"]))
                .Where(x => x.Id != null && x.Name != null)
                .Select(x => new ArtistRef(x.Id!, x.Name!, null))
                .ToList();
            _trackArtists[trackId] = result;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
        finally
        {
            _gate.Release();
        }
    }

    public void Save()
    {
        lock (_saveLock)
        {
            // Drop pages nobody needed for a month, so the file doesn't grow forever.
            foreach (var old in _artists.Where(p => DateTime.UtcNow - p.Value.FetchedUtc > TimeSpan.FromDays(30)).Select(p => p.Key).ToList())
                _artists.TryRemove(old, out _);
            try { Storage.Save(ArtistsFile, new Dictionary<string, ArtistPage>(_artists)); } catch { /* cache only */ }
            try { Storage.Save(TrackArtistsFile, new Dictionary<string, List<ArtistRef>>(_trackArtists)); } catch { /* cache only */ }
        }
    }

    // ~300 px: sharp on tiles without downloading the 640 px version.
    private static string? PickImage(JsonNode? sources)
    {
        var list = (sources as JsonArray)?.Where(s => (string?)s?["url"] != null).ToList() ?? [];
        if (list.Count == 0) return null;
        static int Width(JsonNode? s) => (int?)s?["width"] ?? 0;
        var best = list.Where(s => Width(s) is >= 250 and <= 400).OrderBy(Width).FirstOrDefault()
                   ?? list.OrderByDescending(Width).First();
        return (string?)best!["url"];
    }
}
