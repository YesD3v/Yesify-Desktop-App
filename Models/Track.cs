using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yesify.Models;

public sealed partial class Track : ObservableObject
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string Album { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string? SpotifyId { get; init; }

    /// <summary>From Spotify; null when unknown (YouTube tracks, songs liked before this was recorded).</summary>
    public bool? IsExplicit { get; init; }

    // Playlist embeds have no per-track artwork, so covers are filled in later.
    [ObservableProperty] private string? _imageUrl;

    // Set when the track came from YouTube directly, or once a Spotify track has been matched.
    public string? YouTubeId { get; set; }

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isLiked;

    // Row number in the list currently on screen.
    [JsonIgnore] public int Number { get; set; }

    [JsonIgnore] public string DurationText => Format(Duration);

    [JsonIgnore] public string PrimaryArtist => Artist.Split(',')[0].Trim();

    /// <summary>Identity used for Liked Songs, stable across different playlists.</summary>
    [JsonIgnore]
    public string Key => SpotifyId != null ? "sp:" + SpotifyId
        : YouTubeId != null && Album == "YouTube" ? "yt:" + YouTubeId
        : $"{Artist}|{Title}".ToLowerInvariant();

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}

// Stored in settings.json by number; keep these values stable.
public enum CollectionKind { Playlist = 1, Album = 2, YouTubePlaylist = 4 }

public sealed class PlaylistInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Owner { get; init; } = "";
    public string? ImageUrl { get; init; }
    public CollectionKind Kind { get; init; } = CollectionKind.Playlist;
}

/// <summary>
/// One lyric line. Synced lines have <see cref="Time"/> and <see cref="End"/>; their <see cref="Words"/> carry
/// per-word timing (exact when the source has it, otherwise spread over the line) for the karaoke sweep.
/// </summary>
public sealed record LyricLine(TimeSpan? Time, string Text)
{
    public TimeSpan? End { get; set; }
    public IReadOnlyList<LyricWord> Words { get; set; } = [];

    /// <summary>Instrumental break: shown as three dots that fill up.</summary>
    public bool IsBreak { get; init; }

    /// <summary>True when <see cref="Words"/> carry real per-word timing from the source (not estimated).</summary>
    public bool IsWordTimed { get; init; }
}

public sealed record LyricWord(string Text, TimeSpan Start, TimeSpan End);

public enum QueueSource { Queued, Context, History }

/// <summary>A row in the Queue panel; <see cref="Index"/> points into the list it came from.</summary>
public sealed record QueueItem(Track Track, QueueSource Source, int Index);

/// <summary>Placeholder item: the header at the top of the song list.</summary>
public sealed class HeaderMarker;

public enum LibraryItemKind { Liked, UserPlaylist, Saved, NewPlaylist, Import }

/// <summary>A tile in the Library grid.</summary>
public sealed record LibraryItem(LibraryItemKind Kind, string Title, string Subtitle, string? Image, string? Id = null, PlaylistInfo? Saved = null);

public enum RepeatMode { Off, All, One }
