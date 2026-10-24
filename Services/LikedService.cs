using Yesify.Models;

namespace Yesify.Services;

/// <summary>Liked Songs, stored locally in liked.json (newest first).</summary>
public sealed class LikedService
{
    private const string FileName = "liked.json";

    private readonly List<Track> _tracks = Storage.Load<List<Track>>(FileName);
    private readonly HashSet<string> _keys;

    public LikedService()
    {
        _keys = _tracks.Select(t => t.Key).ToHashSet();
        foreach (var t in _tracks)
        {
            t.IsLiked = true;
            // Spotify songs are re-matched (and cached) by YouTubeService; older saved matches may be clean edits.
            if (t.SpotifyId != null) t.YouTubeId = null;
        }
    }

    public IReadOnlyList<Track> Tracks => _tracks;

    public bool Contains(Track t) => _keys.Contains(t.Key);

    public void Add(Track t)
    {
        if (!_keys.Add(t.Key)) return;
        // Store a copy so later playlist loads don't share (and mutate) the liked entry.
        _tracks.Insert(0, new Track
        {
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
            Duration = t.Duration,
            SpotifyId = t.SpotifyId,
            IsExplicit = t.IsExplicit,
            ImageUrl = t.ImageUrl,
            YouTubeId = t.SpotifyId == null ? t.YouTubeId : null,
            IsLiked = true,
        });
        Save();
    }

    public void Remove(Track t)
    {
        if (!_keys.Remove(t.Key)) return;
        _tracks.RemoveAll(x => x.Key == t.Key);
        Save();
    }

    private void Save() => Storage.Save(FileName, _tracks);
}
