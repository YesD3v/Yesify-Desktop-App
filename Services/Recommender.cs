using Yesify.Models;

namespace Yesify.Services;

public enum HomeItemKind { Mix, Album, Artist, Track }

/// <summary>A tile on Home. Mixes carry their songs; a song tile carries its row so playing continues through it.</summary>
public sealed class HomeItem
{
    public HomeItemKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string? ImageUrl { get; set; }
    public string? Id { get; init; }
    public string Description { get; init; } = "";
    public List<Track>? Tracks { get; set; }
    public int Index { get; init; }
}

public sealed class HomeSection
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public List<HomeItem> Items { get; init; } = [];
}

public sealed class HomeData
{
    public DateTime BuiltUtc { get; init; }
    public List<HomeSection> Sections { get; init; } = [];
}

/// <summary>
/// Builds Home from what you play, like Spotify's home page, using only public Spotify pages.
/// <list type="number">
/// <item><b>Taste.</b> Each artist gets a score from your plays (a full listen counts, a quick skip counts
/// against), likes and library, with older plays fading out (half-life three weeks).</item>
/// <item><b>Neighbourhood.</b> Pixie (Eksombatchai et al., "Pixie: A System for Recommending 3+ Billion Items
/// to 200+ Million Users in Real-Time", WWW 2018): short random walks with restarts from your top artists
/// over Spotify's "Fans also like" graph, which Spotify builds from what millions of people listen to.
/// Artists the walks reach often are close to your taste; an artist reached from several of yours is boosted
/// (Pixie's multi-hit boosting), so the picks reflect your whole taste, not just your #1 artist.</item>
/// <item><b>Songs.</b> Each artist's most popular songs, weighted by how close the artist is, with at most two
/// per artist, never two in a row, and none you already know in the discovery mix.</item>
/// </list>
/// Results change once a day (or on refresh) because the walks are seeded by the date.
/// </summary>
public sealed class Recommender(SpotifyArtistService spotify)
{
    private const double HalfLifeDays = 21;
    private const int SeedCount = 12;
    private const int FetchBudget = 40; // new artist pages per build; cached ones are free
    private const double StopChance = 0.35; // walks average about three hops
    private const int WalkSteps = 6000;

    private sealed class Taste
    {
        public readonly Dictionary<string, double> Artists = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> Tracks = new();
        public readonly Dictionary<string, Track> TrackInfo = new();
        public readonly HashSet<string> KnownArtists = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> KnownTracks = [];
        public readonly HashSet<string> KnownTitles = new(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<HomeData> BuildAsync(IReadOnlyList<ListenEvent> history, IReadOnlyList<Track> liked, IReadOnlyList<Track> library,
                                           int seed, CancellationToken ct)
    {
        var taste = MeasureTaste(history, liked, library);
        var rng = new Random(seed);
        var sections = new List<HomeSection>();

        // 1. Your top artists, as Spotify artists (ids come from one of your songs by each).
        var seeds = await ResolveSeedsAsync(taste, ct);
        await Task.WhenAll(seeds.Select(s => spotify.GetArtistAsync(s.Id, ct)));
        seeds = seeds.Where(s => spotify.Cached(s.Id) != null).ToList();

        // 2. Walk the "Fans also like" graph, fetching the most promising unknown artists between rounds.
        var scores = new Dictionary<string, double>();
        var fetched = 0;
        for (var round = 0; round < 3 && seeds.Count > 0; round++)
        {
            scores = Walk(seeds, rng);
            var frontier = scores.Where(s => spotify.Cached(s.Key) == null).OrderByDescending(s => s.Value)
                .Take((FetchBudget - fetched) / (3 - round)).Select(s => s.Key).ToList();
            if (frontier.Count == 0) break;
            fetched += frontier.Count;
            await Task.WhenAll(frontier.Select(id => spotify.GetArtistAsync(id, ct)));
        }
        if (seeds.Count > 0) scores = Walk(seeds, rng);
        spotify.Save();

        var seedIds = seeds.Select(s => s.Id).ToHashSet();
        var newArtists = scores
            .Where(s => !seedIds.Contains(s.Key) && spotify.Cached(s.Key) is { } p && !taste.KnownArtists.Contains(p.Name))
            .OrderByDescending(s => s.Value)
            .Select(s => (Page: spotify.Cached(s.Key)!, Score: s.Value))
            .ToList();

        // ---- Made for you: a discovery mix, then Daily Mixes around your top artists
        var madeForYou = new List<HomeItem>();
        var discover = DiscoverSongs(newArtists, taste, rng, 30);
        if (discover.Count > 0)
        {
            madeForYou.Add(new HomeItem
            {
                Kind = HomeItemKind.Mix,
                Title = "Discover Mix",
                Subtitle = "New to you: " + JoinNames(discover.Select(t => PrimaryArtist(t.Artist)).Distinct().Take(3)),
                Description = "Songs you haven't played by artists that fans of your favourites listen to. New picks every day.",
                ImageUrl = discover.Select(t => t.ImageUrl).FirstOrDefault(u => u != null),
                Tracks = discover,
                Id = "discover",
            });
        }
        madeForYou.AddRange(DailyMixes(seeds, scores, taste, rng));
        if (madeForYou.Count > 0)
            sections.Add(new HomeSection { Title = "Made for you", Subtitle = "Mixes built from what you play", Items = madeForYou });

        // ---- Recommended songs: the top of the discovery list, playable straight from Home
        if (discover.Count > 0)
        {
            var picks = discover.Take(12).ToList();
            sections.Add(new HomeSection
            {
                Title = "Songs you might like",
                Subtitle = "Based on the artists you listen to",
                Items = picks.Select((t, i) => TrackItem(t, picks, i)).ToList(),
            });
        }

        // ---- New releases from your artists (this year and last)
        var year = DateTime.Now.Year;
        var releases = seeds
            .SelectMany((s, rank) => spotify.Cached(s.Id)!.Releases.Where(r => r.Year >= year - 1).Select(r => (r, s, rank)))
            .DistinctBy(x => x.r.Id)
            // Albums before singles: labels re-release old songs as "new" singles in bulk.
            .OrderByDescending(x => x.r.Year).ThenBy(x => x.r.Type == "SINGLE" ? 1 : 0).ThenBy(x => x.rank)
            .GroupBy(x => x.s.Id).SelectMany(g => g.Take(2))
            .OrderByDescending(x => x.r.Year).ThenBy(x => x.rank)
            .Take(12)
            .Select(x => new HomeItem
            {
                Kind = HomeItemKind.Album,
                Title = x.r.Name,
                Subtitle = $"{(x.r.Type == "SINGLE" ? "Single" : x.r.Type == "EP" ? "EP" : "Album")} · {spotify.Cached(x.s.Id)!.Name} · {x.r.Year}",
                ImageUrl = x.r.ImageUrl,
                Id = x.r.Id,
            })
            .ToList();
        if (releases.Count > 0)
            sections.Add(new HomeSection { Title = "New releases for you", Subtitle = "From artists you listen to", Items = releases });

        // ---- Your top artists
        if (seeds.Count > 0)
            sections.Add(new HomeSection
            {
                Title = "Your top artists",
                Items = seeds.Select(s => ArtistItem(spotify.Cached(s.Id)!, "Artist")).ToList(),
            });

        // ---- Artists you might like
        var suggested = newArtists.Take(12).Select(x => ArtistItem(x.Page, BecauseOf(x.Page.Id, seeds))).ToList();
        if (suggested.Count > 0)
            sections.Add(new HomeSection { Title = "Artists you might like", Subtitle = "Fans of your favourites listen to them", Items = suggested });

        // ---- Recently played
        var recent = history.Where(e => e.Seconds >= 20).DistinctBy(e => e.Key).Take(12)
            .Select(e => new Track { Title = e.Title, Artist = e.Artist, SpotifyId = e.SpotifyId, ImageUrl = e.ImageUrl })
            .ToList();
        if (recent.Count > 0)
            sections.Add(new HomeSection { Title = "Recently played", Items = recent.Select((t, i) => TrackItem(t, recent, i)).ToList() });

        return new HomeData { BuiltUtc = DateTime.UtcNow, Sections = sections };
    }

    // ================================================================ taste

    private static Taste MeasureTaste(IReadOnlyList<ListenEvent> history, IReadOnlyList<Track> liked, IReadOnlyList<Track> library)
    {
        var taste = new Taste();
        var now = DateTime.UtcNow;

        void Credit(string artists, double weight)
        {
            var names = SplitArtists(artists);
            for (var i = 0; i < names.Count; i++)
            {
                // The main artist gets full credit, featured ones half; a skip only counts against the main one.
                var w = i == 0 ? weight : weight > 0 ? weight * 0.5 : 0;
                taste.Artists[names[i]] = taste.Artists.GetValueOrDefault(names[i]) + w;
            }
        }

        void Know(Track t)
        {
            taste.KnownTracks.Add(t.Key);
            taste.KnownTitles.Add(TitleKey(t.Title, t.Artist));
            foreach (var a in SplitArtists(t.Artist)) taste.KnownArtists.Add(a);
            if (t.SpotifyId != null) taste.TrackInfo.TryAdd(t.Key, t);
        }

        foreach (var e in history)
        {
            var decay = Math.Pow(0.5, (now - e.AtUtc).TotalDays / HalfLifeDays);
            var signal = e.Fraction >= 0.8 ? 1.0 : e.Fraction >= 0.4 ? 0.5 : e.Seconds < 30 ? -0.4 : 0.1;
            Credit(e.Artist, signal * decay);
            taste.Tracks[e.Key] = taste.Tracks.GetValueOrDefault(e.Key) + signal * decay;
            var t = new Track { Title = e.Title, Artist = e.Artist, SpotifyId = e.SpotifyId, ImageUrl = e.ImageUrl };
            Know(t);
        }
        foreach (var t in liked)
        {
            Credit(t.Artist, 1.5);
            taste.Tracks[t.Key] = taste.Tracks.GetValueOrDefault(t.Key) + 1.5;
            Know(t);
        }
        foreach (var t in library)
        {
            Credit(t.Artist, 0.1);
            Know(t);
        }
        return taste;
    }

    /// <summary>Your top artists that Spotify knows, with their ids, best first.</summary>
    private async Task<List<ArtistRef>> ResolveSeedsAsync(Taste taste, CancellationToken ct)
    {
        var top = taste.Artists.Where(a => a.Value > 0).OrderByDescending(a => a.Value).Select(a => a.Key).Take(SeedCount * 2).ToList();
        var seeds = new List<ArtistRef>();
        foreach (var name in top)
        {
            if (seeds.Count >= SeedCount) break;
            if (seeds.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            // A Spotify song of yours credited to this artist, main artist first.
            var song = taste.TrackInfo.Values
                .Where(t => SplitArtists(t.Artist).Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(t => PrimaryArtist(t.Artist).Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (song?.SpotifyId == null) continue;
            var artists = await spotify.GetTrackArtistsAsync(song.SpotifyId, ct);
            if (artists.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match && seeds.All(s => s.Id != match.Id))
                seeds.Add(match);
        }
        return seeds;
    }

    // ================================================================ Pixie walk

    /// <summary>
    /// Random walks with restarts from each seed (more steps for the artists you play more), returning
    /// each reached artist's boosted visit count: (Σ over seeds of √visits)².
    /// </summary>
    private Dictionary<string, double> Walk(List<ArtistRef> seeds, Random rng)
    {
        var perSeed = new List<Dictionary<string, int>>();
        // Earlier seeds are your bigger artists; give them more of the walk, but not all of it.
        var weights = seeds.Select((_, i) => 1.0 / Math.Sqrt(i + 1)).ToList();
        var total = weights.Sum();

        for (var s = 0; s < seeds.Count; s++)
        {
            var visits = new Dictionary<string, int>();
            var steps = (int)(WalkSteps * weights[s] / total);
            var current = seeds[s].Id;
            for (var i = 0; i < steps; i++)
            {
                var page = spotify.Cached(current);
                if (page == null || page.Related.Count == 0 || rng.NextDouble() < StopChance)
                {
                    current = seeds[s].Id; // restart
                    page = spotify.Cached(current);
                    if (page == null || page.Related.Count == 0) break;
                }
                // "Fans also like" is ordered by similarity: earlier entries are likelier next steps.
                var next = PickWeighted(page.Related, (_, rank) => 1.0 / (1 + 0.3 * rank), rng);
                visits[next.Id] = visits.GetValueOrDefault(next.Id) + 1;
                current = next.Id;
            }
            perSeed.Add(visits);
        }

        var boosted = new Dictionary<string, double>();
        foreach (var visits in perSeed)
            foreach (var (id, n) in visits)
                boosted[id] = boosted.GetValueOrDefault(id) + Math.Sqrt(n);
        foreach (var id in boosted.Keys.ToList()) boosted[id] *= boosted[id];
        return boosted;
    }

    // ================================================================ songs

    /// <summary>Popular songs by artists close to your taste that you haven't played, liked or saved.</summary>
    private static List<Track> DiscoverSongs(List<(ArtistPage Page, double Score)> artists, Taste taste, Random rng, int count)
    {
        if (artists.Count == 0) return [];
        var max = artists.Max(a => a.Score);
        var candidates = new List<(Track Track, double Score)>();
        foreach (var (page, score) in artists.Take(40))
        {
            var taken = 0;
            for (var rank = 0; rank < page.TopTracks.Count && taken < 2; rank++)
            {
                var t = page.TopTracks[rank];
                if (taste.KnownTracks.Contains("sp:" + t.Id) || taste.KnownTitles.Contains(TitleKey(t.Title, t.Artist))) continue;
                candidates.Add((t.ToTrack(), score / max * Popularity(rank) * Jitter(rng)));
                taken++;
            }
        }
        return Spread(candidates.OrderByDescending(c => c.Score).Select(c => c.Track).DistinctBy(t => TitleKey(t.Title, t.Artist)).Take(count).ToList());
    }

    /// <summary>
    /// Daily Mixes: one per group of your top artists (artists that list each other as similar share a
    /// mix). About 60% songs you know and love by them, the rest their popular songs and their neighbours'.
    /// </summary>
    private List<HomeItem> DailyMixes(List<ArtistRef> seeds, Dictionary<string, double> scores, Taste taste, Random rng)
    {
        var groups = new List<List<ArtistPage>>();
        foreach (var s in seeds)
        {
            var page = spotify.Cached(s.Id)!;
            var group = groups.FirstOrDefault(g => g.Any(p => p.Related.Any(r => r.Id == page.Id) || page.Related.Any(r => r.Id == p.Id)));
            if (group != null) group.Add(page);
            else if (groups.Count < 5) groups.Add([page]);
        }

        var mixes = new List<HomeItem>();
        foreach (var group in groups)
        {
            var names = group.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Songs you know by these artists, the ones you play and like most first.
            var known = taste.TrackInfo.Values
                .Where(t => SplitArtists(t.Artist).Any(names.Contains))
                .Select(t => (Track: t, Score: taste.Tracks.GetValueOrDefault(t.Key) + 0.2) )
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score * Jitter(rng))
                .Select(x => x.Track)
                .Take(18)
                .ToList();

            // Their popular songs, then their closest neighbours' (by walk score).
            var fresh = group.SelectMany(p => p.TopTracks.Take(6).Select((t, r) => (t, s: Popularity(r) * 2)))
                .Concat(group.SelectMany(p => p.Related.Take(6))
                    .DistinctBy(r => r.Id)
                    .Where(r => spotify.Cached(r.Id) != null)
                    .SelectMany(r => spotify.Cached(r.Id)!.TopTracks.Take(3).Select((t, rank) => (t, s: Popularity(rank) * (1 + Math.Log(1 + scores.GetValueOrDefault(r.Id))) / 10))))
                .Where(x => !taste.KnownTracks.Contains("sp:" + x.t.Id))
                .OrderByDescending(x => x.s * Jitter(rng))
                .Select(x => x.t.ToTrack())
                .Take(12)
                .ToList();

            var songs = Interleave(known, fresh, 3, 2).DistinctBy(t => TitleKey(t.Title, t.Artist)).Take(30).ToList();
            if (songs.Count < 8) continue;

            var featured = group.Select(p => p.Name)
                .Concat(group.SelectMany(p => p.Related.Take(2)).Select(r => r.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            mixes.Add(new HomeItem
            {
                Kind = HomeItemKind.Mix,
                Title = $"Daily Mix {mixes.Count + 1}",
                Subtitle = string.Join(", ", featured) + " and more",
                Description = $"{JoinNames(group.Select(p => p.Name))}, with songs you love and some you haven't heard yet.",
                ImageUrl = group[0].ImageUrl,
                Tracks = Spread(songs),
                Id = $"daily-{mixes.Count + 1}",
            });
        }
        return mixes;
    }

    // ================================================================ helpers

    private static HomeItem TrackItem(Track t, List<Track> row, int index) => new()
    {
        Kind = HomeItemKind.Track,
        Title = t.Title,
        Subtitle = t.Artist,
        ImageUrl = t.ImageUrl,
        Id = t.SpotifyId,
        Tracks = row,
        Index = index,
    };

    private static HomeItem ArtistItem(ArtistPage p, string subtitle) => new()
    {
        Kind = HomeItemKind.Artist,
        Title = p.Name,
        Subtitle = subtitle,
        ImageUrl = p.ImageUrl,
        Id = p.Id,
    };

    /// <summary>"Because you like X": the seed whose "Fans also like" lists this artist, if any.</summary>
    private string BecauseOf(string artistId, List<ArtistRef> seeds) =>
        seeds.Select(s => spotify.Cached(s.Id)).FirstOrDefault(p => p != null && p.Related.Any(r => r.Id == artistId)) is { } by
            ? $"Fans of {by.Name} like them"
            : "Suggested for you";

    private static double Popularity(int rank) => 1.0 / (1 + 0.35 * rank);

    private static double Jitter(Random rng) => 0.85 + rng.NextDouble() * 0.3;

    private static T PickWeighted<T>(IReadOnlyList<T> items, Func<T, int, double> weight, Random rng)
    {
        var weights = items.Select((x, i) => weight(x, i)).ToList();
        var r = rng.NextDouble() * weights.Sum();
        for (var i = 0; i < items.Count; i++)
        {
            r -= weights[i];
            if (r <= 0) return items[i];
        }
        return items[^1];
    }

    /// <summary>Takes <paramref name="a"/> items from the first list, then <paramref name="b"/> from the second, and so on.</summary>
    private static IEnumerable<Track> Interleave(List<Track> first, List<Track> second, int a, int b)
    {
        int i = 0, j = 0;
        while (i < first.Count || j < second.Count)
        {
            for (var k = 0; k < a && i < first.Count; k++) yield return first[i++];
            for (var k = 0; k < b && j < second.Count; k++) yield return second[j++];
        }
    }

    /// <summary>Reorders so the same main artist never plays twice in a row when that can be avoided.</summary>
    private static List<Track> Spread(List<Track> songs)
    {
        var left = songs.ToList();
        var result = new List<Track>();
        while (left.Count > 0)
        {
            var last = result.Count > 0 ? PrimaryArtist(result[^1].Artist) : null;
            var next = left.FirstOrDefault(t => !PrimaryArtist(t.Artist).Equals(last, StringComparison.OrdinalIgnoreCase)) ?? left[0];
            left.Remove(next);
            result.Add(next);
        }
        return result;
    }

    private static List<string> SplitArtists(string artists) =>
        artists.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

    private static string PrimaryArtist(string artists) => SplitArtists(artists).FirstOrDefault() ?? "";

    private static string TitleKey(string title, string artist)
    {
        var t = title;
        var cut = t.IndexOfAny(['(', '[']);
        if (cut > 0) t = t[..cut];
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        return $"{PrimaryArtist(artist)}|{t.Trim()}".ToLowerInvariant();
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
