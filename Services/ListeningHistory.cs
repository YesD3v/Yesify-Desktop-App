using Yesify.Models;

namespace Yesify.Services;

/// <summary>One time a song was played: how far it got tells a full listen from a skip.</summary>
public sealed record ListenEvent(string Key, string? SpotifyId, string Title, string Artist, string? ImageUrl, DateTime AtUtc, double Fraction, double Seconds);

/// <summary>
/// What the user played, newest first, kept in listening.json for Home's recommendations. Only this PC
/// sees it; it never leaves the machine.
/// </summary>
public sealed class ListeningHistory
{
    private const string FileName = "listening.json";
    private const int Cap = 2000;

    private readonly List<ListenEvent> _events = Storage.Load<List<ListenEvent>>(FileName);

    public IReadOnlyList<ListenEvent> Events => _events;

    /// <summary>Records a play that lasted <paramref name="listened"/> of <paramref name="duration"/>.</summary>
    public void Record(Track track, TimeSpan listened, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return;
        var fraction = duration > TimeSpan.Zero ? Math.Clamp(listened / duration, 0, 1) : 0;
        _events.Insert(0, new ListenEvent(track.Key, track.SpotifyId, track.Title, track.Artist, track.ImageUrl,
                                          DateTime.UtcNow, Math.Round(fraction, 3), Math.Round(listened.TotalSeconds)));
        if (_events.Count > Cap) _events.RemoveRange(Cap, _events.Count - Cap);
        try { Storage.Save(FileName, _events); } catch { /* history only */ }
    }

    public void Clear()
    {
        _events.Clear();
        try { Storage.Save(FileName, _events); } catch { /* history only */ }
    }
}
