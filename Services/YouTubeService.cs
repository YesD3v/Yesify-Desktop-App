using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Search;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using Yesify.Models;

namespace Yesify.Services;

/// <summary>
/// Finds and downloads audio for tracks from YouTube via YoutubeExplode.
/// Audio is cached on disk so replays and prefetched "next" tracks start instantly. The cache stays under
/// <see cref="MaxCacheBytes"/>: the playing and next songs are always kept, songs that were only prefetched
/// or skipped go first, and songs played often and recently are kept longest.
/// </summary>
public sealed class YouTubeService
{
    public const long MaxCacheBytes = 100L * 1024 * 1024;
    private const string StatsFile = "audio-cache.json";

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yesify", "cache");

    private static readonly string[] Unwanted =
        ["live", "cover", "karaoke", "remix", "sped up", "slowed", "nightcore", "8d", "instrumental", "reaction", "acoustic", "1 hour", "loop",
         "clean", "radio edit", "censored", "edited version"];

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(90);

    private readonly YoutubeClient _yt = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _downloads = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _matches = new();

    /// <summary>How each cached video has been used, keyed by video id.</summary>
    public sealed class UsageStats
    {
        public int Plays { get; set; }
        public int Finished { get; set; }
        public DateTime LastPlayed { get; set; }
    }

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, UsageStats> Usage = Storage.Load<Dictionary<string, UsageStats>>(StatsFile);
    private Track? _current, _next;
    private int _prefetchGeneration;

    public YouTubeService()
    {
        Directory.CreateDirectory(CacheDir);
        foreach (var f in Directory.EnumerateFiles(CacheDir, "*.part"))
            try { File.Delete(f); } catch { /* in use */ }
        TrimCache();
    }

    /// <summary>Returns a local audio file for the track, downloading it if needed.</summary>
    public async Task<string> GetAudioFileAsync(Track track, CancellationToken ct)
    {
        // Songs removed from Spotify come back with no title and no artist. Searching for nothing finds
        // some random video, so don't play them at all (this also drops wrong matches saved earlier).
        if (track.SpotifyId != null && string.IsNullOrWhiteSpace(track.Title))
        {
            track.YouTubeId = null;
            throw new InvalidOperationException("this song isn't available on Spotify anymore");
        }

        // Spotify songs always go through the matcher (instant once matched), so a wrong video saved in a
        // playlist or session by an older version gets replaced.
        if (track.YouTubeId == null || track.SpotifyId != null)
        {
            // Share one search between "play now" and "prefetch" so they can't pick different videos.
            var match = _matches.GetOrAdd(track.Key, _ => new Lazy<Task<string>>(() => FindVideoIdAsync(track, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token)));
            try
            {
                track.YouTubeId = await match.Value.WaitAsync(ct);
            }
            finally
            {
                if (match.Value.IsCompleted) _matches.TryRemove(track.Key, out _);
            }
        }
        var id = track.YouTubeId;

        var cached = Directory.EnumerateFiles(CacheDir, id + ".*").FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".tmp"));
        if (cached != null)
        {
            File.SetLastAccessTimeUtc(cached, DateTime.UtcNow);
            Mp4Fixer.RemoveEditList(cached); // files cached by older versions
            return cached;
        }

        // Share one download between "play now" and "prefetch"; the download itself is not cancelled
        // by a skip, so a half-finished file is still useful if the user comes back to it.
        var lazy = _downloads.GetOrAdd(id, key => new Lazy<Task<string>>(() => DownloadWithRetryAsync(key)));
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<string> DownloadWithRetryAsync(string videoId)
    {
        try
        {
            try
            {
                return await DownloadAsync(videoId);
            }
            catch (Exception e) when (e is not InvalidOperationException)
            {
                // Network hiccups and stalled streams usually succeed on a fresh attempt.
                return await DownloadAsync(videoId);
            }
        }
        finally
        {
            _downloads.TryRemove(videoId, out _);
        }
    }

    private async Task<string> DownloadAsync(string videoId)
    {
        using var timeout = new CancellationTokenSource(DownloadTimeout);
        var manifest = await _yt.Videos.Streams.GetManifestAsync(videoId, timeout.Token);
        var audio = manifest.GetAudioOnlyStreams().ToList();
        // Media Foundation decodes AAC (mp4) natively; Opus/WebM is only a fallback.
        var stream = audio.Where(s => s.Container == Container.Mp4).OrderByDescending(s => s.Bitrate).FirstOrDefault()
                     ?? audio.OrderByDescending(s => s.Bitrate).FirstOrDefault()
                     ?? throw new InvalidOperationException("No audio stream available for this video.");

        var path = Path.Combine(CacheDir, $"{videoId}.{(stream.Container == Container.Mp4 ? "m4a" : stream.Container.Name)}");
        var part = path + ".part";
        try
        {
            await _yt.Videos.Streams.DownloadAsync(stream, part, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The download stalled.");
        }
        Mp4Fixer.RemoveEditList(part);
        File.Move(part, path, overwrite: true);
        TrimCache(except: videoId); // it's about to play (or be next)
        return path;
    }

    /// <summary>
    /// Call when a song starts playing: counts the play, keeps it and the next song in the cache, and
    /// downloads the next song a few seconds later (only if the user hasn't moved on by then).
    /// </summary>
    public void NowPlaying(Track current, Track? next)
    {
        _current = current;
        if (current.YouTubeId != null)
        {
            lock (CacheLock)
            {
                var stats = Usage.TryGetValue(current.YouTubeId, out var s) ? s : Usage[current.YouTubeId] = new UsageStats();
                stats.Plays++;
                stats.LastPlayed = DateTime.UtcNow;
            }
            SaveUsage();
        }
        Prefetch(next);
    }

    /// <summary>Call when a song played to its end; finished songs are kept over skipped ones.</summary>
    public void Finished(Track track)
    {
        if (track.YouTubeId == null) return;
        lock (CacheLock)
            if (Usage.TryGetValue(track.YouTubeId, out var stats)) stats.Finished++;
        SaveUsage();
    }

    /// <summary>Downloads the song that plays next, after a short wait so fast skipping doesn't download every song.</summary>
    public void Prefetch(Track? track)
    {
        _next = track;
        var generation = Interlocked.Increment(ref _prefetchGeneration);
        if (track == null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (generation != _prefetchGeneration) return; // another song started or the queue changed
            try { await GetAudioFileAsync(track, CancellationToken.None); } catch { /* best effort */ }
        });
    }
    // ---------------------------------------------------------------- matching

    /// <summary>A search hit from either YouTube Music (clean song titles) or regular YouTube.</summary>
    private sealed record Candidate(string VideoId, string Title, string Artists, TimeSpan? Duration, bool? IsExplicit, bool FromMusic);

    // v3: matches made before every credited artist was searched (and wrong artists penalised) may be a
    // different song with the same title; v2 also predates telling explicit and clean versions apart.
    private const string MatchCacheFile = "matches-v4.json"; // v3 could hold songs with the wrong title

    private readonly ConcurrentDictionary<string, string> _matchCache =
        new(Storage.Load<Dictionary<string, string>>(MatchCacheFile));
    private readonly object _matchSaveLock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private async Task<string> FindVideoIdAsync(Track track, CancellationToken ct)
    {
        if (_matchCache.TryGetValue(track.Key, out var known)) return known;

        var title = CleanTitle(track.Title);

        // Search with each credited artist, then all of them together: the first one can be something a
        // search engine can't use (e.g. "¥$"), and then the right song isn't in its results at all. Stop as
        // soon as a result matches both title and artist.
        var artists = track.Artist.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        var queries = artists.Select(a => $"{a} {title}").ToList();
        if (artists.Count > 1) queries.Add($"{string.Join(" ", artists)} {title}");
        if (queries.Count == 0) queries.Add(title);

        // YouTube Music's song search returns official audio and marks explicit versions; regular YouTube
        // search is the fallback if it finds nothing at all.
        var scored = new List<(Candidate C, double Score)>();
        var seen = new HashSet<string>();
        foreach (var query in queries)
        {
            List<Candidate> found;
            try { found = await SearchMusicAsync(query, ct); }
            catch (OperationCanceledException) { throw; }
            catch { found = []; }
            for (var i = 0; i < found.Count; i++)
                if (seen.Add(found[i].VideoId)) scored.Add((found[i], Score(found[i], i, track, title)));
            if (scored.Any(x => IsSameSong(x.C, track, title))) break;
        }
        // Unreleased and leaked songs often aren't on YouTube Music; regular YouTube may have an upload.
        if (!scored.Any(x => IsSameSong(x.C, track, title)))
        {
            List<Candidate> found;
            try { found = await SearchVideosAsync($"{track.PrimaryArtist} - {title}", ct); }
            catch (OperationCanceledException) { throw; }
            catch { found = []; }
            foreach (var (c, i) in found.Select((c, i) => (c, i)))
                if (seen.Add(c.VideoId)) scored.Add((c, Score(c, i, track, title)));
        }
        // A result without the song's title is another song (often a hit by the same artist), and one by
        // none of its artists is a different song with the same name: playing nothing beats playing those.
        scored.RemoveAll(x => title.Length > 0 ? !IsSameSong(x.C, track, title) : !ArtistMatches(x.C, track));
        if (scored.Count == 0) throw new InvalidOperationException($"couldn't find \"{track.Artist} - {title}\" on YouTube");

        var best = scored.OrderByDescending(x => x.Score).First().C.VideoId;

        _matchCache[track.Key] = best;
        lock (_matchSaveLock)
        {
            try { Storage.Save(MatchCacheFile, new Dictionary<string, string>(_matchCache)); } catch { /* cache only */ }
        }
        return best;
    }

    /// <summary>Does any of the track's credited artists appear in the result's artists (or title)?</summary>
    private static bool ArtistMatches(Candidate c, Track track)
    {
        static string Squash(string s) => new(s.ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        var haystack = Squash(c.Artists + " " + c.Title);
        return track.Artist.Split(',').Select(a => Squash(a)).Any(a => a.Length > 0 && haystack.Contains(a));
    }

    /// <summary>
    /// Is the result titled as the song, ignoring case, spaces and punctuation? YouTube Music titles must be
    /// the same song name ("Fly Away" is not "I'll Fly Away"); video titles only have to contain it.
    /// </summary>
    private static bool TitleMatches(Candidate c, string cleanTitle)
    {
        static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var want = Squash(cleanTitle);
        if (want.Length == 0) return c.Title.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase);
        return c.FromMusic ? Squash(CleanTitle(c.Title)) == want : Squash(c.Title).Contains(want);
    }

    private static bool IsSameSong(Candidate c, Track track, string cleanTitle) =>
        TitleMatches(c, cleanTitle) && ArtistMatches(c, track);

    private static double Score(Candidate c, int rank, Track track, string cleanTitle)
    {
        var title = c.Title.ToLowerInvariant();
        var artists = c.Artists.ToLowerInvariant();
        var original = track.Title.ToLowerInvariant();

        double s = -rank * 2;
        if (TitleMatches(c, cleanTitle)) s += 30;
        // A song with the right title by someone else is a different song: only take it if nothing better exists.
        s += ArtistMatches(c, track) ? 20 : -60;
        if (artists.EndsWith("- topic")) s += 25; // auto-generated official audio uploads
        if (title.Contains("official audio")) s += 5;

        foreach (var word in Unwanted)
            if (title.Contains(word) && !original.Contains(word)) s -= 25;

        // Clean versions mute the swearing; only pick one when Spotify's version is clean too.
        if (c.IsExplicit is { } explicitHit)
        {
            if (track.IsExplicit == true) s += explicitHit ? 40 : -40;
            else if (track.IsExplicit == false) s += explicitHit ? -10 : 0;
            else s += explicitHit ? 10 : 0; // unknown: prefer uncensored
        }

        if (c.Duration is { } d && track.Duration > TimeSpan.Zero)
        {
            var diff = Math.Abs((d - track.Duration).TotalSeconds);
            s -= Math.Min(diff, 180) * 0.5;
            if (diff <= 3) s += 15;
        }
        return s;
    }

    /// <summary>Song search on YouTube Music (the public InnerTube API its website uses; no account).</summary>
    private async Task<List<Candidate>> SearchMusicAsync(string query, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["context"] = new JsonObject
            {
                ["client"] = new JsonObject { ["clientName"] = "WEB_REMIX", ["clientVersion"] = "1.20250101.01.00", ["hl"] = "en", ["gl"] = "US" },
            },
            ["query"] = query,
            ["params"] = "EgWKAQIIAWoKEAkQBRAKEAMQBA==", // filter: songs
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.youtube.com/youtubei/v1/search?prettyPrint=false")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        req.Headers.Add("Origin", "https://music.youtube.com");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));

        var results = new List<Candidate>();
        foreach (var item in FindAll(json, "musicResponsiveListItemRenderer"))
        {
            var videoId = (string?)item["playlistItemData"]?["videoId"];
            var columns = item["flexColumns"]?.AsArray();
            if (videoId == null || columns == null || columns.Count < 2) continue;

            static string Text(JsonNode? col) => string.Concat(
                col?["musicResponsiveListItemFlexColumnRenderer"]?["text"]?["runs"]?.AsArray().Select(r => (string?)r?["text"]) ?? []);

            // Second column reads "Artist(s) • Album • 3:08".
            var parts = Text(columns[1]).Split(" • ");
            var explicitBadge = item["badges"]?.ToJsonString().Contains("MUSIC_EXPLICIT_BADGE") == true;
            results.Add(new Candidate(videoId, Text(columns[0]), parts[0], ParseDuration(parts[^1]), explicitBadge, true));
            if (results.Count >= 10) break;
        }
        return results;
    }

    private async Task<List<Candidate>> SearchVideosAsync(string query, CancellationToken ct)
    {
        var results = new List<Candidate>();
        await foreach (var v in _yt.Search.GetVideosAsync(query, ct))
        {
            // Regular YouTube can't tell explicit from clean, so lean on the title.
            results.Add(new Candidate(v.Id.Value, v.Title, v.Author.ChannelTitle, v.Duration, null, false));
            if (results.Count >= 10) break;
        }
        return results;
    }

    private static IEnumerable<JsonNode> FindAll(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (k == key && v != null) yield return v;
                    foreach (var x in FindAll(v, key)) yield return x;
                }
                break;
            case JsonArray a:
                foreach (var v in a)
                    foreach (var x in FindAll(v, key)) yield return x;
                break;
        }
    }

    private static TimeSpan? ParseDuration(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3 || !parts.All(p => int.TryParse(p, out _))) return null;
        var n = parts.Select(int.Parse).ToArray();
        return n.Length == 3 ? new TimeSpan(n[0], n[1], n[2]) : new TimeSpan(0, n[0], n[1]);
    }

    // "Song (feat. X) - Remastered 2011" -> "Song"
    private static string CleanTitle(string title)
    {
        var t = title;
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        var paren = t.IndexOfAny(['(', '[']);
        if (paren > 0) t = t[..paren];
        return t.Trim();
    }
    // ---------------------------------------------------------------- browsing

    public async Task<List<Track>> SearchAsync(string query, CancellationToken ct)
    {
        var results = new List<Track>();
        await foreach (var v in _yt.Search.GetVideosAsync(query, ct))
        {
            // Skip hour-long mixes and livestreams; this is a song search.
            if (v.Duration is not { } d || d > TimeSpan.FromMinutes(15)) continue;
            results.Add(FromVideo(v.Id, v.Title, v.Author.ChannelTitle, v.Duration, v.Thumbnails));
            if (results.Count >= 30) break;
        }
        return results;
    }

    public async Task<Track> GetVideoAsync(VideoId id, CancellationToken ct)
    {
        var v = await _yt.Videos.GetAsync(id, ct);
        return FromVideo(v.Id, v.Title, v.Author.ChannelTitle, v.Duration, v.Thumbnails);
    }

    public async Task<(PlaylistInfo Info, List<Track> Tracks)> GetPlaylistAsync(PlaylistId id, CancellationToken ct)
    {
        var p = await _yt.Playlists.GetAsync(id, ct);
        var tracks = new List<Track>();
        await foreach (var v in _yt.Playlists.GetVideosAsync(id, ct))
        {
            tracks.Add(FromVideo(v.Id, v.Title, v.Author.ChannelTitle, v.Duration, v.Thumbnails));
            if (tracks.Count >= 5000) break;
        }
        var info = new PlaylistInfo
        {
            Id = id.Value,
            Name = p.Title,
            Owner = p.Author?.ChannelTitle ?? "YouTube",
            ImageUrl = p.Thumbnails.Count > 0 ? p.Thumbnails.GetWithHighestResolution().Url : tracks.FirstOrDefault()?.ImageUrl,
            Kind = CollectionKind.YouTubePlaylist,
        };
        return (info, tracks);
    }

    private static Track FromVideo(VideoId id, string title, string channel, TimeSpan? duration, IReadOnlyList<Thumbnail> thumbs)
    {
        var artist = channel.EndsWith(" - Topic") ? channel[..^8] : channel;
        // Strip "Artist - " prefixes from video titles when the channel is the artist.
        if (title.StartsWith(artist + " - ", StringComparison.OrdinalIgnoreCase))
            title = title[(artist.Length + 3)..];

        return new Track
        {
            Title = title,
            Artist = artist,
            Album = "YouTube",
            Duration = duration ?? TimeSpan.Zero,
            ImageUrl = thumbs.Count > 0 ? thumbs.OrderBy(t => t.Resolution.Area).FirstOrDefault(t => t.Resolution.Width >= 300)?.Url ?? thumbs.GetWithHighestResolution().Url : null,
            YouTubeId = id.Value,
        };
    }

    public static long CacheSize()
    {
        try { return new DirectoryInfo(CacheDir).EnumerateFiles().Sum(f => f.Length); }
        catch { return 0; }
    }

    /// <summary>Deletes downloaded audio; files in use (the playing song) are skipped.</summary>
    public static void ClearCache()
    {
        foreach (var f in new DirectoryInfo(CacheDir).EnumerateFiles())
        {
            try { f.Delete(); } catch { /* in use */ }
        }
        lock (CacheLock) Usage.Clear();
        SaveUsage();
    }

    /// <summary>
    /// Deletes the least useful songs until the cache fits its budget. Never touches the playing or next
    /// song, or a download in progress. Songs that never played go first; the rest are ranked by when they
    /// were last played, with a bonus for each time they were played to the end.
    /// </summary>
    private void TrimCache(string? except = null)
    {
        try
        {
            lock (CacheLock)
            {
                var keep = new[] { _current?.YouTubeId, _next?.YouTubeId, except };
                var files = new DirectoryInfo(CacheDir).GetFiles()
                    .Where(f => f.Extension is not (".part" or ".tmp"))
                    .ToList();
                var total = files.Sum(f => f.Length);
                if (total <= MaxCacheBytes) return;

                double Score(FileInfo f)
                {
                    if (!Usage.TryGetValue(Path.GetFileNameWithoutExtension(f.Name), out var s) || s.Plays == 0)
                        return f.LastWriteTimeUtc.Ticks / (double)TimeSpan.TicksPerDay - 100_000; // never played
                    var bonus = Math.Min(s.Finished, 10) * 2.0 - (s.Finished == 0 ? 3 : 0); // days
                    return s.LastPlayed.Ticks / (double)TimeSpan.TicksPerDay + bonus;
                }

                foreach (var f in files.Where(f => !keep.Contains(Path.GetFileNameWithoutExtension(f.Name))).OrderBy(Score))
                {
                    if (total <= MaxCacheBytes) break;
                    try
                    {
                        var size = f.Length;
                        f.Delete();
                        total -= size;
                    }
                    catch { /* playing or locked */ }
                }

                // Forget songs that are no longer on disk, so the stats file stays small.
                var onDisk = new DirectoryInfo(CacheDir).GetFiles().Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToHashSet();
                foreach (var id in Usage.Keys.Where(id => !onDisk.Contains(id)).ToList()) Usage.Remove(id);
            }
            SaveUsage();
        }
        catch { /* cache trimming is best effort */ }
    }

    private static void SaveUsage()
    {
        try
        {
            lock (CacheLock) Storage.Save(StatsFile, Usage);
        }
        catch { /* stats only */ }
    }
}
