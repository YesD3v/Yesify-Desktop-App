using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yesify.Models;

namespace Yesify.Services;

/// <summary>
/// Reads public playlists, albums and tracks without any account:
/// <list type="bullet">
/// <item>Spotify's embed widget page (open.spotify.com/embed/...) gives the first 100 tracks instantly.</item>
/// <item>The anonymous token that same page hands every visitor lets us read the full track list and
/// per-track details from Spotify's player backend (spclient). That backend is undocumented, so everything
/// built on it is best effort: if it fails, callers keep the first 100 tracks.</item>
/// </list>
/// </summary>
public sealed partial class SpotifyEmbedService
{
    public const int PlaylistCap = 100;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // v2 added the explicit flag.
    private const string TrackCacheFile = "spotify-tracks-v2.json";
    private const string Base62 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private readonly SemaphoreSlim _tokenLock = new(1);
    private readonly SemaphoreSlim _gate = new(8);
    private readonly ConcurrentDictionary<string, CachedTrack> _trackCache;
    private string? _token;
    private DateTime _tokenExpiresUtc;

    public SpotifyEmbedService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Yesify/1.0");
        _trackCache = new ConcurrentDictionary<string, CachedTrack>(Storage.Load<Dictionary<string, CachedTrack>>(TrackCacheFile));
    }

    public sealed record CachedTrack(string Title, string Artist, string Album, long DurationMs, string? ImageUrl, bool Explicit);

    [GeneratedRegex("""<script id="__NEXT_DATA__" type="application/json">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextData();

    public async Task<(PlaylistInfo Info, List<Track> Tracks)> GetAsync(string kind, string id, CancellationToken ct)
    {
        var html = await _http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}", ct);
        var match = NextData().Match(html);
        var state = match.Success ? JsonNode.Parse(match.Groups[1].Value)?["props"]?["pageProps"]?["state"] : null;
        RememberToken(state?["settings"]?["session"]);
        var entity = state?["data"]?["entity"];
        if (entity == null) throw new InvalidOperationException($"Couldn't read that Spotify {kind}. Is it public?");

        var name = (string?)entity["name"] ?? (string?)entity["title"] ?? "Spotify";
        var image = PickImage(entity);

        if (kind == "track")
        {
            var track = new Track
            {
                Title = name,
                Artist = string.Join(", ", entity["artists"]?.AsArray().Select(a => (string?)a?["name"]).OfType<string>() ?? []),
                Duration = TimeSpan.FromMilliseconds((double?)entity["duration"] ?? 0),
                ImageUrl = image,
                SpotifyId = id,
                IsExplicit = (bool?)entity["isExplicit"],
            };
            return (new PlaylistInfo { Id = id, Name = name, Owner = track.Artist, ImageUrl = image }, [track]);
        }

        var isAlbum = kind == "album";
        var tracks = (entity["trackList"]?.AsArray() ?? [])
            .Where(t => t != null && (string?)t["entityType"] is null or "track")
            .Select(t => new Track
            {
                Title = (string?)t!["title"] ?? "Unknown",
                Artist = (string?)t["subtitle"] ?? "",
                Album = isAlbum ? name : "",
                // Playlist embeds don't include per-track artwork.
                ImageUrl = isAlbum ? image : null,
                Duration = TimeSpan.FromMilliseconds((double?)t["duration"] ?? 0),
                SpotifyId = ((string?)t["uri"])?.Split(':').LastOrDefault(),
                IsExplicit = (bool?)t["isExplicit"],
            })
            .ToList();

        var info = new PlaylistInfo
        {
            Id = id,
            Name = name,
            Owner = (string?)entity["subtitle"] ?? "",
            ImageUrl = image,
            Kind = isAlbum ? CollectionKind.Album : CollectionKind.Playlist,
        };
        return (info, tracks);
    }

    // ---------------------------------------------------------------- full playlists (spclient)

    private void RememberToken(JsonNode? session)
    {
        if ((string?)session?["accessToken"] is not { } token) return;
        _token = token;
        var expiresMs = (long?)session?["accessTokenExpirationTimestampMs"];
        _tokenExpiresUtc = expiresMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.AddMinutes(-2)
            : DateTime.UtcNow.AddMinutes(30);
    }

    private async Task<string> GetTokenAsync(string playlistId, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token == null || DateTime.UtcNow >= _tokenExpiresUtc)
                await GetAsync("playlist", playlistId, ct); // refreshes the token as a side effect
            return _token ?? throw new InvalidOperationException("Spotify didn't hand out a token.");
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<JsonNode> GetBackendJsonAsync(string url, string playlistId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(playlistId, ct));
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                await Task.Delay(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt), ct);
                continue;
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _token = null; // expired early: fetch a fresh one
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        }
    }

    /// <summary>All item URIs of a playlist, in order (tracks, episodes and local files).</summary>
    public async Task<List<string>> GetPlaylistUrisAsync(string playlistId, CancellationToken ct)
    {
        var uris = new List<string>();
        while (true)
        {
            var json = await GetBackendJsonAsync(
                $"https://spclient.wg.spotify.com/playlist/v2/playlist/{playlistId}?from={uris.Count}&length=500", playlistId, ct);
            var items = json["contents"]?["items"]?.AsArray() ?? [];
            uris.AddRange(items.Select(i => (string?)i?["uri"]).OfType<string>());
            var total = (int?)json["length"] ?? 0;
            if (items.Count == 0 || json["contents"]?["truncated"]?.GetValue<bool>() != true || uris.Count >= total) return uris;
        }
    }

    /// <summary>
    /// Track details for playlist item URIs, in the same order. Episodes are skipped; local files are
    /// described from their URI. Results are cached on disk.
    /// </summary>
    public async Task<List<Track>> GetTracksAsync(IReadOnlyList<string> uris, string playlistId, CancellationToken ct)
    {
        var results = await Task.WhenAll(uris.Select(u => GetTrackAsync(u, playlistId, ct)));
        try { Storage.Save(TrackCacheFile, new Dictionary<string, CachedTrack>(_trackCache)); } catch { /* cache only */ }
        return results.OfType<Track>().ToList();
    }

    private async Task<Track?> GetTrackAsync(string uri, string playlistId, CancellationToken ct)
    {
        if (uri.StartsWith("spotify:local:")) return ParseLocalFile(uri);
        if (!uri.StartsWith("spotify:track:")) return null; // podcast episodes

        var id = uri["spotify:track:".Length..];
        if (!_trackCache.TryGetValue(id, out var cached))
        {
            await _gate.WaitAsync(ct);
            try
            {
                var json = await GetBackendJsonAsync(
                    $"https://spclient.wg.spotify.com/metadata/4/track/{ToHexGid(id)}?market=from_token", playlistId, ct);
                var cover = json["album"]?["cover_group"]?["image"]?.AsArray()
                    .FirstOrDefault(i => (string?)i?["size"] == "DEFAULT") ?? json["album"]?["cover_group"]?["image"]?.AsArray().FirstOrDefault();
                cached = new CachedTrack(
                    (string?)json["name"] ?? "Unknown",
                    string.Join(", ", json["artist"]?.AsArray().Select(a => (string?)a?["name"]).OfType<string>() ?? []),
                    (string?)json["album"]?["name"] ?? "",
                    (long?)json["duration"] ?? 0,
                    (string?)cover?["file_id"] is { } file ? "https://i.scdn.co/image/" + file : null,
                    (bool?)json["explicit"] ?? false);
                _trackCache[id] = cached;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; } // one bad track shouldn't sink the playlist
            finally
            {
                _gate.Release();
            }
        }

        return new Track
        {
            Title = cached.Title,
            Artist = cached.Artist,
            Album = cached.Album,
            Duration = TimeSpan.FromMilliseconds(cached.DurationMs),
            ImageUrl = cached.ImageUrl,
            SpotifyId = id,
            IsExplicit = cached.Explicit,
        };
    }

    // spotify:local:Artist:Album:Title:DurationSeconds, URL-encoded with '+' for spaces.
    private static Track? ParseLocalFile(string uri)
    {
        var p = uri.Split(':');
        if (p.Length < 6) return null;
        static string D(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
        var title = D(p[4]);
        if (title.Length == 0) return null;
        return new Track
        {
            Title = title,
            Artist = D(p[2]),
            Album = D(p[3]),
            Duration = TimeSpan.FromSeconds(int.TryParse(p[5], out var s) ? s : 0),
        };
    }

    // Spotify IDs are base62; the backend wants the 128-bit value as 32 hex digits.
    private static string ToHexGid(string base62)
    {
        BigInteger n = 0;
        foreach (var ch in base62) n = n * 62 + Base62.IndexOf(ch);
        return n.ToString("x32")[^32..];
    }

    // ---------------------------------------------------------------- links

    /// <summary>Extracts (kind, id) from open.spotify.com links or spotify: URIs.</summary>
    public static (string Kind, string Id)? ParseLink(string text)
    {
        text = text.Trim();
        if (text.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            var p = text.Split(':');
            return p.Length >= 3 ? (p[1].ToLowerInvariant(), p[2]) : null;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !s.StartsWith("intl-", StringComparison.OrdinalIgnoreCase) && s != "embed").ToArray();
            if (segs.Length >= 2) return (segs[0].ToLowerInvariant(), segs[1]);
        }
        return null;
    }

    // Prefer a ~300px image from coverArt.sources or visualIdentity.image.
    private static string? PickImage(JsonNode entity)
    {
        var candidates = (entity["coverArt"]?["sources"]?.AsArray() ?? [])
            .Concat(entity["visualIdentity"]?["image"]?.AsArray() ?? [])
            .Where(i => (string?)i?["url"] != null)
            .ToList();
        if (candidates.Count == 0) return null;

        static int Width(JsonNode? i) => (int?)i?["maxWidth"] ?? (int?)i?["width"] ?? 0;
        var best = candidates.Where(i => Width(i) is >= 200 and <= 700).OrderBy(Width).FirstOrDefault() ?? candidates[0];
        return (string?)best!["url"];
    }
}
