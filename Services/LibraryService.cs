using System.IO;
using Yesify.Models;

namespace Yesify.Services;

/// <summary>A playlist made in Yesify.</summary>
public sealed class UserPlaylist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Playlist";
    public string Description { get; set; } = "";
    /// <summary>Who made it (shown under the title); empty for your own.</summary>
    public string Author { get; set; } = "";
    /// <summary>A picked image, copied into Yesify's folder.</summary>
    public string? CoverPath { get; set; }
    /// <summary>Artwork carried over when copying a Spotify/YouTube playlist or album.</summary>
    public string? CoverUrl { get; set; }
    public List<Track> Tracks { get; set; } = [];
    public DateTime Created { get; set; } = DateTime.Now;

    /// <summary>What to show as the cover: the picked image, the copied artwork, or the first song's cover.</summary>
    public string? DisplayCover => CoverPath ?? CoverUrl ?? Tracks.FirstOrDefault(t => t.ImageUrl != null)?.ImageUrl;
}

/// <summary>
/// The user's own playlists (playlists.json), and the last-loaded songs of imported playlists
/// (library-tracks.json) so the library can be searched without reloading every playlist.
/// </summary>
public sealed class LibraryService
{
    private const string PlaylistsFile = "playlists.json";
    private const string TrackCacheFile = "library-tracks.json";
    public static readonly string CoversDir = Path.Combine(Storage.AppDataDir, "covers");

    private readonly List<UserPlaylist> _playlists = Storage.Load<List<UserPlaylist>>(PlaylistsFile);
    private readonly Dictionary<string, List<Track>> _imported = Storage.Load<Dictionary<string, List<Track>>>(TrackCacheFile);

    public IReadOnlyList<UserPlaylist> Playlists => _playlists;

    public UserPlaylist? Find(string id) => _playlists.FirstOrDefault(p => p.Id == id);

    public UserPlaylist Create(string name, string description, string author, string? pickedCover, string? coverUrl, IEnumerable<Track> tracks)
    {
        var p = new UserPlaylist { Name = name, Description = description, Author = author, CoverUrl = coverUrl, Tracks = tracks.Select(Copy).ToList() };
        if (pickedCover != null) p.CoverPath = StoreCover(p.Id, pickedCover);
        _playlists.Insert(0, p);
        Save();
        return p;
    }

    public void Update(UserPlaylist p, string name, string description, string author, string? pickedCover)
    {
        p.Name = name;
        p.Description = description;
        p.Author = author;
        if (pickedCover != null && pickedCover != p.CoverPath)
        {
            var old = p.CoverPath;
            p.CoverPath = StoreCover(p.Id, pickedCover);
            if (old != null && old != p.CoverPath) TryDelete(old);
        }
        Save();
    }

    public void Delete(UserPlaylist p)
    {
        _playlists.Remove(p);
        if (p.CoverPath != null) TryDelete(p.CoverPath);
        Save();
    }

    /// <summary>Adds songs, skipping ones already in the playlist. Returns how many were added.</summary>
    public int Add(UserPlaylist p, IEnumerable<Track> tracks)
    {
        var have = p.Tracks.Select(t => t.Key).ToHashSet();
        var added = 0;
        foreach (var t in tracks)
        {
            if (!have.Add(t.Key)) continue;
            p.Tracks.Add(Copy(t));
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public void RemoveAt(UserPlaylist p, int index)
    {
        if (index < 0 || index >= p.Tracks.Count) return;
        p.Tracks.RemoveAt(index);
        Save();
    }

    /// <summary>Remembers an imported playlist's songs for library search.</summary>
    public void CacheImported(string collectionId, IEnumerable<Track> tracks)
    {
        _imported[collectionId] = tracks.Select(Copy).ToList();
        try { Storage.Save(TrackCacheFile, _imported); } catch { /* cache only */ }
    }

    /// <summary>The remembered songs of an imported playlist, if it has been opened since it was saved.</summary>
    public List<Track>? ImportedTracks(string collectionId) =>
        _imported.TryGetValue(collectionId, out var list) ? list.Select(Copy).ToList() : null;

    public void ForgetImported(string collectionId)
    {
        if (!_imported.Remove(collectionId)) return;
        try { Storage.Save(TrackCacheFile, _imported); } catch { /* cache only */ }
    }

    /// <summary>Every song in the library (your playlists and remembered imported ones), each once.</summary>
    public IEnumerable<Track> AllTracks(IEnumerable<Track> liked)
    {
        var seen = new HashSet<string>();
        foreach (var t in liked.Concat(_playlists.SelectMany(p => p.Tracks)).Concat(_imported.Values.SelectMany(l => l)))
            if (seen.Add(t.Key)) yield return t;
    }

    private void Save() => Storage.Save(PlaylistsFile, _playlists);

    // Copy the picked image into Yesify's folder (unique name, so the image cache sees the change).
    private static string StoreCover(string id, string source)
    {
        Directory.CreateDirectory(CoversDir);
        var target = Path.Combine(CoversDir, $"{id}-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(source).ToLowerInvariant()}");
        File.Copy(source, target, overwrite: true);
        return target;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* in use or gone */ }
    }

    // Playlists keep their own copy of each song, independent of where it came from.
    private static Track Copy(Track t) => new()
    {
        Title = t.Title,
        Artist = t.Artist,
        Album = t.Album,
        Duration = t.Duration,
        SpotifyId = t.SpotifyId,
        IsExplicit = t.IsExplicit,
        ImageUrl = t.ImageUrl,
        YouTubeId = t.SpotifyId == null ? t.YouTubeId : null,
    };
}
