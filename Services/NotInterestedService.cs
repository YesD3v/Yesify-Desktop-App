using Yesify.Models;

namespace Yesify.Services;

/// <summary>
/// Songs, artists and Home tiles you said you're not interested in. Home's recommendations (Discover Mix,
/// Daily Mixes' new songs, "Songs you might like", "Artists you might like") leave them out, and a song you
/// turn down counts a little against its artist. Kept in not-interested.json, on this PC only.
/// </summary>
public sealed class NotInterestedService
{
    private const string FileName = "not-interested.json";

    public sealed class Data
    {
        /// <summary>Song keys ("sp:…", "yt:…") and title keys ("artist|title"), so other versions count too.</summary>
        public HashSet<string> Songs { get; set; } = [];
        /// <summary>The main artists of the songs turned down (each one counts against them a little).</summary>
        public List<string> SongArtists { get; set; } = [];
        public HashSet<string> Artists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Home tiles: "Kind:Id".</summary>
        public HashSet<string> Items { get; set; } = [];
    }

    private readonly Data _data = Load();

    private static Data Load()
    {
        var d = Storage.Load<Data>(FileName);
        d.Artists = new HashSet<string>(d.Artists, StringComparer.OrdinalIgnoreCase);
        return d;
    }

    public int Count => _data.Songs.Count / 2 + _data.Artists.Count + _data.Items.Count;

    /// <summary>A copy for the recommender (it runs on a background thread).</summary>
    public Data Snapshot() => new()
    {
        Songs = [.. _data.Songs],
        SongArtists = [.. _data.SongArtists],
        Artists = new HashSet<string>(_data.Artists, StringComparer.OrdinalIgnoreCase),
        Items = [.. _data.Items],
    };

    public static string TitleKey(string title, string artist)
    {
        var t = title;
        var cut = t.IndexOfAny(['(', '[']);
        if (cut > 0) t = t[..cut];
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        var main = artist.Split(',')[0].Trim();
        return $"{main}|{t.Trim()}".ToLowerInvariant();
    }

    public static string ItemKey(HomeItem item) => $"{item.Kind}:{item.Id ?? item.Title}";

    public bool Has(Track t) => _data.Songs.Contains(t.Key) || _data.Songs.Contains(TitleKey(t.Title, t.Artist));
    public bool Has(HomeItem item) => _data.Items.Contains(ItemKey(item))
                                      || item.Kind is HomeItemKind.Artist or HomeItemKind.MusicArtist && _data.Artists.Contains(item.Title);

    public void Add(Track t)
    {
        _data.Songs.Add(t.Key);
        _data.Songs.Add(TitleKey(t.Title, t.Artist));
        var main = t.Artist.Split(',')[0].Trim();
        if (main.Length > 0) _data.SongArtists.Add(main);
        Save();
    }

    /// <summary>A Home tile: never shown again; for an artist, none of their songs are recommended either.</summary>
    public void Add(HomeItem item)
    {
        _data.Items.Add(ItemKey(item));
        if (item.Kind is HomeItemKind.Artist or HomeItemKind.MusicArtist) _data.Artists.Add(item.Title);
        Save();
    }

    public void Clear()
    {
        _data.Songs.Clear();
        _data.SongArtists.Clear();
        _data.Artists.Clear();
        _data.Items.Clear();
        Save();
    }

    private void Save() => Storage.SaveInBackground(FileName, Snapshot());
}
