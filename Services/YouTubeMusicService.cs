using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yesify.Models;

namespace Yesify.Services;

/// <summary>A page from YouTube Music: an artist, an album, a playlist, Explore, a genre...</summary>
public sealed class MusicPage
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ImageUrl { get; set; }

    /// <summary>The page's songs (an artist's top songs, an album's or playlist's tracks).</summary>
    public List<Track> Songs { get; } = [];

    /// <summary>Label over <see cref="Songs"/> when they're only part of the page ("Top songs").</summary>
    public string SongsTitle { get; set; } = "";

    /// <summary>Playlist with all of an artist's songs ("VL…" browse id), for "See all songs".</summary>
    public string? AllSongsId { get; set; }

    /// <summary>The artist's Mix: an endless radio built from their songs.</summary>
    public string? RadioVideoId { get; set; }
    public string? RadioPlaylistId { get; set; }

    /// <summary>Rows of albums, artists, playlists, genres and songs below the songs.</summary>
    public List<HomeSection> Sections { get; } = [];

    /// <summary>Set for albums and playlists, which can be saved to the library.</summary>
    public PlaylistInfo? Collection { get; set; }
}

/// <summary>What a search finds on YouTube Music besides songs.</summary>
public sealed class MusicSearchResults
{
    public List<Track> Songs { get; init; } = [];
    public List<HomeItem> Artists { get; init; } = [];
    public List<HomeItem> Albums { get; init; } = [];
    public List<HomeItem> Playlists { get; init; } = [];
}

/// <summary>
/// YouTube Music through the public InnerTube API its own website uses (no account, no key): search,
/// artists (top songs, albums, singles, videos, playlists, "Fans might also like"), albums, playlists,
/// Explore (new releases, trending, charts, moods &amp; genres) and radio. Songs play the exact upload
/// YouTube Music lists, so they don't need matching.
/// </summary>
public sealed partial class YouTubeMusicService
{
    private const string Api = "https://music.youtube.com/youtubei/v1/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Search filters, as YouTube Music's own filter chips send them.
    private const string SongsFilter = "EgWKAQIIAWoKEAkQBRAKEAMQBA==";
    private const string ArtistsFilter = "EgWKAQIgAWoKEAkQBRAKEAMQBA==";
    private const string AlbumsFilter = "EgWKAQIYAWoKEAkQBRAKEAMQBA==";
    private const string FeaturedPlaylistsFilter = "EgeKAQQoADgBagwQDhAKEAMQBRAJEAQ=";
    private const string CommunityPlaylistsFilter = "EgeKAQQoAEABagoQCRAFEAoQAxAE";

    private async Task<JsonNode?> PostAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        body["context"] = new JsonObject
        {
            ["client"] = new JsonObject { ["clientName"] = "WEB_REMIX", ["clientVersion"] = "1.20250101.01.00", ["hl"] = "en", ["gl"] = "US" },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + endpoint + "?prettyPrint=false")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        req.Headers.Add("Origin", "https://music.youtube.com");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private Task<JsonNode?> BrowseRawAsync(string browseId, string? @params, CancellationToken ct)
    {
        var body = new JsonObject { ["browseId"] = browseId };
        if (!string.IsNullOrEmpty(@params)) body["params"] = Uri.UnescapeDataString(@params);
        return PostAsync("browse", body, ct);
    }

    // ================================================================ search

    public async Task<List<Track>> SearchSongsAsync(string query, CancellationToken ct) =>
        ListItems(await PostAsync("search", new JsonObject { ["query"] = query, ["params"] = SongsFilter }, ct))
            .Select(i => ToTrack(i)).OfType<Track>().Take(30).ToList();

    /// <summary>Artists, albums and playlists matching the query (songs come from <see cref="SearchSongsAsync"/>).</summary>
    public async Task<MusicSearchResults> SearchAsync(string query, CancellationToken ct)
    {
        async Task<List<HomeItem>> Find(string filter)
        {
            try { return ListItems(await PostAsync("search", new JsonObject { ["query"] = query, ["params"] = filter }, ct)).Select(ToItem).OfType<HomeItem>().ToList(); }
            catch (OperationCanceledException) { throw; }
            catch { return []; }
        }

        var artists = Find(ArtistsFilter);
        var albums = Find(AlbumsFilter);
        var featured = Find(FeaturedPlaylistsFilter);
        var community = Find(CommunityPlaylistsFilter);
        await Task.WhenAll(artists, albums, featured, community);
        return new MusicSearchResults
        {
            Artists = artists.Result.Where(i => i.Kind == HomeItemKind.MusicArtist).Take(12).ToList(),
            Albums = albums.Result.Where(i => i.Kind == HomeItemKind.MusicAlbum).Take(16).ToList(),
            Playlists = featured.Result.Concat(community.Result).Where(i => i.Kind == HomeItemKind.MusicPlaylist).DistinctBy(i => i.Id).Take(16).ToList(),
        };
    }

    private static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The YouTube Music artist with this name (e.g. for a song from Spotify), or null.</summary>
    public async Task<HomeItem?> FindArtistAsync(string name, CancellationToken ct)
    {
        var found = ListItems(await PostAsync("search", new JsonObject { ["query"] = name, ["params"] = ArtistsFilter }, ct))
            .Select(ToItem).OfType<HomeItem>().Where(i => i.Kind == HomeItemKind.MusicArtist).ToList();
        return found.FirstOrDefault(a => Squash(a.Title) == Squash(name)) ?? found.FirstOrDefault();
    }

    /// <summary>The YouTube Music album with this title by this artist, or null.</summary>
    public async Task<HomeItem?> FindAlbumAsync(string album, string artist, CancellationToken ct)
    {
        var found = ListItems(await PostAsync("search", new JsonObject { ["query"] = $"{artist} {album}", ["params"] = AlbumsFilter }, ct))
            .Select(ToItem).OfType<HomeItem>().Where(i => i.Kind == HomeItemKind.MusicAlbum).ToList();
        // Exact title first, then the same title without edition tags ("Graduation (Deluxe Edition)").
        // Never "contains": "Yeezus 2" (unreleased) must not open "Yeezus". The artist has to match too.
        var want = Squash(album);
        var wantBase = AlbumBase(album);
        var who = Squash(artist);
        bool ByArtist(HomeItem a) => who.Length == 0 || Squash(a.Subtitle ?? "").Contains(who) || Squash(a.Title).Contains(who);
        return found.FirstOrDefault(a => Squash(a.Title) == want && ByArtist(a))
               ?? found.FirstOrDefault(a => wantBase.Length > 0 && AlbumBase(a.Title) == wantBase && ByArtist(a));
    }

    /// <summary>An album title without edition tags: "(Deluxe Edition)", "[Explicit]", " - Remastered".</summary>
    private static string AlbumBase(string title)
    {
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        return Squash(dash > 0 ? title[..dash] : title);
    }

    // ================================================================ pages

    public async Task<MusicPage> ArtistAsync(string channelId, CancellationToken ct)
    {
        var json = await BrowseRawAsync(channelId, null, ct);
        var page = new MusicPage();
        var header = json?["header"]?["musicImmersiveHeaderRenderer"] ?? json?["header"]?["musicVisualHeaderRenderer"];
        page.Title = Text(header?["title"]);
        page.Description = ShortBio(Text(header?["description"]));
        page.ImageUrl = Image(json?["microformat"], 544) ?? Image(header?["thumbnail"], 544) ?? Image(header?["foregroundThumbnail"], 544);
        var audience = Text(header?["monthlyListenerCount"]);
        var subscribers = Text(header?["subscriptionButton"]?["subscribeButtonRenderer"]?["subscriberCountText"]);
        page.Subtitle = audience.Length > 0 ? audience : subscribers.Length > 0 ? $"{subscribers} subscribers" : "Artist";
        var radio = header?["startRadioButton"]?["buttonRenderer"]?["navigationEndpoint"]?["watchEndpoint"];
        page.RadioVideoId = (string?)radio?["videoId"];
        page.RadioPlaylistId = (string?)radio?["playlistId"];

        foreach (var shelf in FindAll(json, "musicShelfRenderer"))
        {
            page.SongsTitle = Text(shelf["title"]);
            page.AllSongsId = (string?)shelf["title"]?["runs"]?[0]?["navigationEndpoint"]?["browseEndpoint"]?["browseId"]
                              ?? (string?)shelf["bottomEndpoint"]?["browseEndpoint"]?["browseId"];
            page.Songs.AddRange(Items(shelf["contents"]).Select(i => ToTrack(i)).OfType<Track>());
            break;
        }
        page.Sections.AddRange(Carousels(json));
        return page;
    }

    public async Task<MusicPage> AlbumAsync(string browseId, CancellationToken ct)
    {
        var json = await BrowseRawAsync(browseId, null, ct);
        var page = new MusicPage();
        var header = FindAll(json, "musicResponsiveHeaderRenderer").FirstOrDefault() ?? FindAll(json, "musicDetailHeaderRenderer").FirstOrDefault();
        page.Title = Text(header?["title"]);
        var artistRuns = header?["straplineTextOne"]?["runs"]?.AsArray();
        var artist = Text(header?["straplineTextOne"]);
        var artistId = artistRuns?.Select(r => BrowseIdOf(r?["navigationEndpoint"])).FirstOrDefault(id => id != null);
        var kindAndYear = Text(header?["subtitle"]);
        var length = Text(header?["secondSubtitle"]);
        page.Subtitle = string.Join(" · ", new[] { artist, kindAndYear.Replace(" • ", " · "), length.Replace(" • ", " · ") }.Where(s => s.Length > 0));
        page.Description = Text(header?["description"]?["musicDescriptionShelfRenderer"]?["description"]);
        if (page.Description.Length > 400) page.Description = page.Description[..400].TrimEnd() + "…";
        page.ImageUrl = Image(header?["thumbnail"], 544);

        var shelf = FindAll(json, "musicShelfRenderer").FirstOrDefault();
        foreach (var item in Items(shelf?["contents"]))
            if (ToTrack(item, artist, artistId, page.Title, browseId, page.ImageUrl) is { } t) page.Songs.Add(t);

        page.Collection = new PlaylistInfo
        {
            Id = browseId,
            Name = page.Title,
            Owner = artist,
            ImageUrl = Image(header?["thumbnail"], 300),
            Kind = CollectionKind.MusicAlbum,
        };
        page.Sections.AddRange(Carousels(json));
        return page;
    }

    /// <summary>A playlist (browse id "VL…"), all of its songs (up to <paramref name="max"/>).</summary>
    public async Task<MusicPage> PlaylistAsync(string browseId, CancellationToken ct, int max = 1000)
    {
        if (!browseId.StartsWith("VL")) browseId = "VL" + browseId;
        var json = await BrowseRawAsync(browseId, null, ct);
        var page = new MusicPage();
        var header = FindAll(json, "musicResponsiveHeaderRenderer").FirstOrDefault() ?? FindAll(json, "musicDetailHeaderRenderer").FirstOrDefault();
        page.Title = Text(header?["title"]);
        var owner = Text(header?["straplineTextOne"]);
        if (owner.Length == 0) owner = Text(header?["subtitle"]).Split(" • ").Skip(1).FirstOrDefault() ?? "";
        page.Subtitle = string.Join(" · ", new[] { owner, Text(header?["secondSubtitle"]).Replace(" • ", " · ") }.Where(s => s.Length > 0));
        page.Description = Text(header?["description"]?["musicDescriptionShelfRenderer"]?["description"]);
        if (page.Description.Length == 0) page.Description = Text(header?["description"]);
        if (page.Description.Length > 400) page.Description = page.Description[..400].TrimEnd() + "…";
        page.ImageUrl = Image(header?["thumbnail"], 544);

        var shelf = FindAll(json, "musicPlaylistShelfRenderer").FirstOrDefault() ?? FindAll(json, "musicShelfRenderer").FirstOrDefault();
        var contents = shelf?["contents"]?.AsArray();
        AddRows(contents);
        var token = Continuation(contents) ?? (string?)shelf?["continuations"]?[0]?["nextContinuationData"]?["continuation"];
        while (token != null && page.Songs.Count < max)
        {
            var more = await PostAsync("browse", new JsonObject { ["continuation"] = token }, ct);
            var rows = more?["onResponseReceivedActions"]?[0]?["appendContinuationItemsAction"]?["continuationItems"]?.AsArray()
                       ?? more?["continuationContents"]?["musicPlaylistShelfContinuation"]?["contents"]?.AsArray();
            if (rows == null || rows.Count == 0) break;
            var before = page.Songs.Count;
            AddRows(rows);
            token = Continuation(rows) ?? (string?)more?["continuationContents"]?["musicPlaylistShelfContinuation"]?["continuations"]?[0]?["nextContinuationData"]?["continuation"];
            if (page.Songs.Count == before) break;
        }

        page.Collection = new PlaylistInfo
        {
            Id = browseId,
            Name = page.Title,
            Owner = owner.Length > 0 ? owner : "YouTube Music",
            ImageUrl = Image(header?["thumbnail"], 300),
            Kind = CollectionKind.MusicPlaylist,
        };
        page.Sections.AddRange(Carousels(json).Where(s => s.Items.Count > 0));
        return page;

        void AddRows(JsonArray? rows)
        {
            foreach (var item in Items(rows))
                if (ToTrack(item) is { } t) page.Songs.Add(t);
        }

        static string? Continuation(JsonArray? rows) =>
            (string?)rows?.LastOrDefault()?["continuationItemRenderer"]?["continuationEndpoint"]?["continuationCommand"]?["token"];
    }

    /// <summary>Any page made of rows (a genre, new releases, an artist's discography...).</summary>
    public async Task<MusicPage> BrowseAsync(string browseId, string? @params, CancellationToken ct)
    {
        var json = await BrowseRawAsync(browseId, @params, ct);
        var page = new MusicPage
        {
            Title = Text(json?["header"]?["musicHeaderRenderer"]?["title"])
                    is { Length: > 0 } t ? t : Text(FindAll(json, "musicHeaderRenderer").FirstOrDefault()?["title"]),
        };
        page.Sections.AddRange(Carousels(json));
        return page;
    }

    /// <summary>Explore: new albums and singles, trending songs, charts and top artists, moods and genres, new videos.</summary>
    public async Task<MusicPage> ExploreAsync(CancellationToken ct)
    {
        var explore = BrowseRawAsync("FEmusic_explore", null, ct);
        var charts = Quietly(BrowseRawAsync("FEmusic_charts", null, ct));
        await Task.WhenAll(explore, charts);

        var page = new MusicPage { Title = "Explore", Subtitle = "New releases, charts, moods and genres from YouTube Music" };
        var all = Carousels(await explore).Concat(Carousels(await charts)).ToList();
        string[] order = ["New albums & singles", "Trending", "Top artists", "Video charts", "Moods & genres", "New music videos"];
        all.RemoveAll(s => s.Title.Length == 0); // the shortcut buttons at the top of YouTube Music's own page
        page.Sections.AddRange(all.OrderBy(s => Array.IndexOf(order, s.Title) is var i and >= 0 ? i : order.Length));
        foreach (var s in page.Sections.Where(s => s.Title == "Video charts")) s.Title = "Charts";
        return page;

        static async Task<JsonNode?> Quietly(Task<JsonNode?> t)
        {
            try { return await t; }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Radio: songs like this one (or an artist's Mix), as YouTube Music's own "Up next" builds it.
    /// </summary>
    public async Task<List<Track>> RadioAsync(string? videoId, string? playlistId, CancellationToken ct, int max = 100)
    {
        var body = new JsonObject();
        if (videoId != null) body["videoId"] = videoId;
        body["playlistId"] = playlistId ?? $"RDAMVM{videoId}";
        body["params"] = "wAEB";
        var json = await PostAsync("next", body, ct);
        var tracks = new List<Track>();
        var seen = new HashSet<string>();
        for (var round = 0; round < 3; round++)
        {
            foreach (var v in FindAll(json, "playlistPanelVideoRenderer"))
                if (PanelTrack(v) is { } t && seen.Add(t.YouTubeId!)) tracks.Add(t);
            var token = (string?)FindAll(json, "nextRadioContinuationData").FirstOrDefault()?["continuation"]
                        ?? (string?)FindAll(json, "nextContinuationData").FirstOrDefault()?["continuation"];
            if (token == null || tracks.Count >= max) break;
            json = await PostAsync("next", new JsonObject { ["continuation"] = token }, ct);
        }
        return tracks.Take(max).ToList();
    }

    private static Track? PanelTrack(JsonNode v)
    {
        var id = (string?)v["videoId"];
        if (id == null) return null;
        var runs = v["longBylineText"]?["runs"]?.AsArray() ?? [];
        var (artist, artistId, album, albumId) = ("", (string?)null, "", (string?)null);
        foreach (var seg in Segments(runs))
        {
            var kind = PageType(seg.FirstOrDefault(r => r?["navigationEndpoint"] != null)?["navigationEndpoint"]);
            var text = string.Concat(seg.Select(r => (string?)r?["text"]));
            if (kind == "MUSIC_PAGE_TYPE_ARTIST" || (artist.Length == 0 && kind == null && !Regex.IsMatch(text, @"^\d{4}$|views|plays")))
            {
                if (artist.Length > 0) continue;
                artist = text;
                artistId = seg.Select(r => BrowseIdOf(r?["navigationEndpoint"])).FirstOrDefault(x => x != null);
            }
            else if (kind == "MUSIC_PAGE_TYPE_ALBUM")
            {
                album = text;
                albumId = seg.Select(r => BrowseIdOf(r?["navigationEndpoint"])).FirstOrDefault(x => x != null);
            }
        }
        return new Track
        {
            Title = Text(v["title"]),
            Artist = artist.Length > 0 ? artist : Text(v["shortBylineText"]),
            Album = album,
            Duration = ParseDuration(Text(v["lengthText"])) ?? TimeSpan.Zero,
            ImageUrl = Image(v, 300),
            YouTubeId = id,
            ArtistId = artistId,
            AlbumId = albumId,
        };
    }

    // ================================================================ parsing

    /// <summary>Rows of tiles (carousels and grids), skipping podcasts.</summary>
    private static IEnumerable<HomeSection> Carousels(JsonNode? json)
    {
        foreach (var (key, shelf) in FindShelves(json))
        {
            var header = shelf["header"]?["musicCarouselShelfBasicHeaderRenderer"] ?? shelf["header"]?["gridHeaderRenderer"];
            var title = Text(header?["title"]);
            if (key == "gridRenderer" && title.Length == 0) title = "";
            var more = header?["moreContentButton"]?["buttonRenderer"]?["navigationEndpoint"]?["browseEndpoint"]
                       ?? header?["title"]?["runs"]?[0]?["navigationEndpoint"]?["browseEndpoint"];
            var items = Items(shelf["contents"] ?? shelf["items"]);

            var section = new HomeSection
            {
                Title = title,
                Wrap = key == "gridRenderer",
                MoreId = (string?)more?["browseId"],
                MoreParams = (string?)more?["params"],
            };
            // Song rows (Trending, an artist's videos) play on through the row.
            var songs = new List<Track>();
            foreach (var item in items)
            {
                if (ToItem(item) is { } tile) section.Items.Add(tile);
                else if (ToTrack(item) is { } t) songs.Add(t);
            }
            for (var i = 0; i < songs.Count; i++)
                section.Items.Add(new HomeItem { Kind = HomeItemKind.Track, Title = songs[i].Title, Subtitle = songs[i].Artist, ImageUrl = songs[i].ImageUrl, Tracks = songs, Index = i });
            if (section.Items.Count == 0) continue;
            if (section.MoreId != null && section.MoreId.StartsWith("FEmusic_moods_and_genres_category")) section.MoreId = null; // already a category page
            yield return section;
        }
    }

    private static IEnumerable<(string Key, JsonNode Shelf)> FindShelves(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (v == null) continue;
                    if (k is "musicCarouselShelfRenderer" or "gridRenderer") { yield return (k, v); continue; }
                    if (k is "musicShelfRenderer" or "musicPlaylistShelfRenderer") continue; // song lists are read separately
                    foreach (var x in FindShelves(v)) yield return x;
                }
                break;
            case JsonArray a:
                foreach (var v in a)
                    foreach (var x in FindShelves(v)) yield return x;
                break;
        }
    }

    /// <summary>The renderer inside each entry of a contents array ({"musicTwoRowItemRenderer": {...}} → its value, tagged).</summary>
    private static IEnumerable<(string Kind, JsonNode Node)> Items(JsonNode? contents)
    {
        if (contents is not JsonArray a) yield break;
        foreach (var entry in a)
            if (entry is JsonObject o && o.FirstOrDefault() is { Value: { } v } kv)
                yield return (kv.Key, v);
    }

    private static IEnumerable<(string Kind, JsonNode Node)> ListItems(JsonNode? json) =>
        FindAll(json, "musicResponsiveListItemRenderer").Select(n => ("musicResponsiveListItemRenderer", n));

    /// <summary>A tile: album, artist, playlist or genre. Songs and videos are tracks, not tiles (null).</summary>
    private static HomeItem? ToItem((string Kind, JsonNode Node) item)
    {
        var (kind, n) = item;
        switch (kind)
        {
            case "musicNavigationButtonRenderer":
            {
                var be = n["clickCommand"]?["browseEndpoint"];
                if ((string?)be?["browseId"] is not { } id) return null;
                var color = (long?)n["solid"]?["leftStripeColor"];
                return new HomeItem
                {
                    Kind = HomeItemKind.MusicCategory,
                    Title = Text(n["buttonText"]),
                    Id = id,
                    Params = (string?)be["params"],
                    Color = color is { } c ? $"#{c & 0xFFFFFFFF:X8}" : null,
                };
            }
            case "musicTwoRowItemRenderer":
            {
                var nav = n["navigationEndpoint"];
                var id = BrowseIdOf(nav);
                if (id == null) return null; // a song or video
                var subtitle = Text(n["subtitle"]).Replace(" • ", " · ");
                return PageType(nav) switch
                {
                    "MUSIC_PAGE_TYPE_ALBUM" => new HomeItem { Kind = HomeItemKind.MusicAlbum, Title = Text(n["title"]), Subtitle = subtitle, Id = id, ImageUrl = Image(n["thumbnailRenderer"], 300) },
                    "MUSIC_PAGE_TYPE_ARTIST" => new HomeItem { Kind = HomeItemKind.MusicArtist, Title = Text(n["title"]), Subtitle = subtitle, Id = id, ImageUrl = Image(n["thumbnailRenderer"], 300) },
                    "MUSIC_PAGE_TYPE_PLAYLIST" => new HomeItem { Kind = HomeItemKind.MusicPlaylist, Title = Text(n["title"]), Subtitle = subtitle, Id = id, ImageUrl = Image(n["thumbnailRenderer"], 300) },
                    _ => null,
                };
            }
            case "musicResponsiveListItemRenderer":
            {
                var nav = n["navigationEndpoint"];
                var id = BrowseIdOf(nav);
                if (id == null) return null;
                var cols = n["flexColumns"]?.AsArray();
                var title = Text(cols?[0]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
                var sub = cols?.Count > 1 ? Text(cols[1]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]) : "";
                // "Artist • 320M monthly audience" → "320M monthly audience"; "Album • Kanye West • 2007" → "Album · Kanye West · 2007"
                var parts = sub.Split(" • ").ToList();
                if (parts.Count > 1 && parts[0] is "Artist" or "Playlist") parts.RemoveAt(0);
                var subtitle = string.Join(" · ", parts);
                var image = Image(n["thumbnail"], 300);
                return PageType(nav) switch
                {
                    "MUSIC_PAGE_TYPE_ARTIST" => new HomeItem { Kind = HomeItemKind.MusicArtist, Title = title, Subtitle = subtitle, Id = id, ImageUrl = image },
                    "MUSIC_PAGE_TYPE_ALBUM" => new HomeItem { Kind = HomeItemKind.MusicAlbum, Title = title, Subtitle = subtitle, Id = id, ImageUrl = image },
                    "MUSIC_PAGE_TYPE_PLAYLIST" => new HomeItem { Kind = HomeItemKind.MusicPlaylist, Title = title, Subtitle = subtitle, Id = id, ImageUrl = image },
                    _ => null,
                };
            }
        }
        return null;
    }

    /// <summary>
    /// A song row (or song/video tile). Artists and albums are read from the links in its columns, so it
    /// works for every list layout; an album's own page passes its artist, title and cover for its rows.
    /// </summary>
    private static Track? ToTrack((string Kind, JsonNode Node) item, string? artist = null, string? artistId = null,
                                  string? album = null, string? albumId = null, string? cover = null)
    {
        var (kind, n) = item;
        if (kind == "musicTwoRowItemRenderer")
        {
            var watch = n["navigationEndpoint"]?["watchEndpoint"];
            if ((string?)watch?["videoId"] is not { } vid) return null;
            var sub = n["subtitle"]?["runs"]?.AsArray() ?? [];
            var seg = Segments(sub).FirstOrDefault(s => s.Any(r => PageType(r?["navigationEndpoint"]) == "MUSIC_PAGE_TYPE_ARTIST"))
                      ?? Segments(sub).FirstOrDefault() ?? [];
            return new Track
            {
                Title = Text(n["title"]),
                Artist = string.Concat(seg.Select(r => (string?)r?["text"])),
                ImageUrl = Image(n["thumbnailRenderer"], 300),
                YouTubeId = vid,
                ArtistId = seg.Select(r => BrowseIdOf(r?["navigationEndpoint"])).FirstOrDefault(x => x != null),
            };
        }
        if (kind != "musicResponsiveListItemRenderer") return null;

        var videoId = (string?)n["playlistItemData"]?["videoId"]
                      ?? (string?)n["overlay"]?["musicItemThumbnailOverlayRenderer"]?["content"]?["musicPlayButtonRenderer"]?["playNavigationEndpoint"]?["watchEndpoint"]?["videoId"];
        var cols = n["flexColumns"]?.AsArray();
        if (videoId == null || cols == null || cols.Count == 0) return null;
        if (BrowseIdOf(n["navigationEndpoint"]) != null) return null; // an artist/album/playlist row

        var title = Text(cols[0]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
        string? foundArtist = null, foundArtistId = null, foundAlbum = null, foundAlbumId = null;
        TimeSpan? duration = null;
        var loose = new List<string>();
        for (var c = 1; c < cols.Count; c++)
        {
            var runs = cols[c]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]?["runs"]?.AsArray();
            if (runs == null) continue;
            foreach (var seg in Segments(runs))
            {
                var text = string.Concat(seg.Select(r => (string?)r?["text"])).Trim();
                if (text.Length == 0) continue;
                var types = seg.Select(r => PageType(r?["navigationEndpoint"])).ToList();
                if (types.Contains("MUSIC_PAGE_TYPE_ARTIST") || types.Contains("MUSIC_PAGE_TYPE_USER_CHANNEL"))
                {
                    if (foundArtist != null) continue;
                    foundArtist = text;
                    foundArtistId = seg.Select(r => PageType(r?["navigationEndpoint"]) == "MUSIC_PAGE_TYPE_ARTIST" ? BrowseIdOf(r?["navigationEndpoint"]) : null).FirstOrDefault(x => x != null);
                }
                else if (types.Contains("MUSIC_PAGE_TYPE_ALBUM"))
                {
                    foundAlbum = text;
                    foundAlbumId = seg.Select(r => BrowseIdOf(r?["navigationEndpoint"])).FirstOrDefault(x => x != null);
                }
                else if (ParseDuration(text) is { } d) duration = d;
                else if (text is "Song" or "Video" or "Episode" || Regex.IsMatch(text, @"\b(plays|views|play|view)$") || Regex.IsMatch(text, @"^\d{4}$")) { }
                else if (c == 1) loose.Add(text);
            }
        }
        foreach (var f in n["fixedColumns"]?.AsArray() ?? [])
            if (ParseDuration(Text(f?["musicResponsiveListItemFixedColumnRenderer"]?["text"])) is { } d) duration = d;

        return new Track
        {
            Title = title,
            Artist = foundArtist ?? loose.FirstOrDefault() ?? artist ?? "",
            ArtistId = foundArtistId ?? (foundArtist == null ? artistId : null),
            Album = foundAlbum ?? album ?? "",
            AlbumId = foundAlbumId ?? (foundAlbum == null ? albumId : null),
            Duration = duration ?? TimeSpan.Zero,
            IsExplicit = n["badges"]?.ToJsonString().Contains("MUSIC_EXPLICIT_BADGE") == true,
            ImageUrl = Image(n["thumbnail"], 300) ?? cover,
            YouTubeId = videoId,
        };
    }

    /// <summary>Splits runs at the " • " separators.</summary>
    private static IEnumerable<List<JsonNode?>> Segments(JsonArray runs)
    {
        var seg = new List<JsonNode?>();
        foreach (var r in runs)
        {
            if (((string?)r?["text"])?.Trim() == "•")
            {
                if (seg.Count > 0) yield return seg;
                seg = [];
            }
            else seg.Add(r);
        }
        if (seg.Count > 0) yield return seg;
    }

    private static string? BrowseIdOf(JsonNode? nav) => (string?)nav?["browseEndpoint"]?["browseId"];

    private static string? PageType(JsonNode? nav) =>
        (string?)nav?["browseEndpoint"]?["browseEndpointContextSupportedConfigs"]?["browseEndpointContextMusicConfig"]?["pageType"];

    /// <summary>The first few sentences of a bio, without the "From Wikipedia…" credit.</summary>
    private static string ShortBio(string bio)
    {
        var cut = bio.IndexOf("From Wikipedia (", StringComparison.Ordinal);
        if (cut >= 0) bio = bio[..cut];
        bio = bio.Trim();
        if (bio.Length <= 320) return bio;
        var end = bio.LastIndexOf(". ", 320, StringComparison.Ordinal);
        return end > 120 ? bio[..(end + 1)] : bio[..320].TrimEnd() + "…";
    }

    private static string Text(JsonNode? t) =>
        t == null ? "" : (string?)t["simpleText"] ?? string.Concat(t["runs"]?.AsArray().Select(r => (string?)r?["text"]) ?? []);

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex SizeSuffix();

    [GeneratedRegex(@"=s\d+")]
    private static partial Regex SquareSuffix();

    /// <summary>The best thumbnail anywhere under the node, asked for at about <paramref name="size"/> px.</summary>
    private static string? Image(JsonNode? node, int size)
    {
        var thumbs = FindAll(node, "thumbnails").FirstOrDefault()?.AsArray();
        var url = (string?)thumbs?.LastOrDefault()?["url"];
        if (url == null) return null;
        if (url.StartsWith("//")) url = "https:" + url;
        if (url.Contains("googleusercontent.com") || url.Contains("ggpht.com"))
        {
            if (SizeSuffix().IsMatch(url)) return SizeSuffix().Replace(url, $"=w{size}-h{size}");
            if (SquareSuffix().IsMatch(url)) return SquareSuffix().Replace(url, $"=s{size}");
        }
        return url;
    }

    private static TimeSpan? ParseDuration(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3 || !parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return null;
        var n = parts.Select(int.Parse).ToArray();
        return n.Length == 3 ? new TimeSpan(n[0], n[1], n[2]) : new TimeSpan(0, n[0], n[1]);
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
}
