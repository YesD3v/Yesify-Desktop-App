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

    private const string StatsFile = "listening-stats.json";

    private readonly List<ListenEvent> _events = Storage.Load<List<ListenEvent>>(FileName);
    private readonly ListeningStats _stats;

    public ListeningHistory()
    {
        _stats = Storage.Load<ListeningStats>(StatsFile);
        if (_stats.Since == default && _events.Count > 0)
        {
            // First run with stats: start from the history kept so far.
            foreach (var e in Enumerable.Reverse(_events)) Count(e);
            Storage.SaveInBackground(StatsFile, _stats.Snapshot());
        }
    }

    public IReadOnlyList<ListenEvent> Events => _events;

    /// <summary>All-time totals for Home's landing page (not capped like the history).</summary>
    public ListeningStats Stats => _stats;

    /// <summary>A song counts as listened to once it played for half a minute (or most of a short one).</summary>
    private void Count(ListenEvent e)
    {
        if (e.Seconds <= 0) return;
        if (_stats.Since == default || e.AtUtc < _stats.Since) _stats.Since = e.AtUtc;
        _stats.Seconds += e.Seconds;
        if (e.Seconds >= 30 || e.Fraction >= 0.5)
        {
            _stats.Plays++;
            _stats.Songs.Add(e.Key);
        }
    }

    /// <summary>Records a play that lasted <paramref name="listened"/> of <paramref name="duration"/>.</summary>
    public void Record(Track track, TimeSpan listened, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return;
        var fraction = duration > TimeSpan.Zero ? Math.Clamp(listened / duration, 0, 1) : 0;
        _events.Insert(0, new ListenEvent(track.Key, track.SpotifyId, track.Title, track.Artist, track.ImageUrl,
                                          DateTime.UtcNow, Math.Round(fraction, 3), Math.Round(listened.TotalSeconds)));
        if (_events.Count > Cap) _events.RemoveRange(Cap, _events.Count - Cap);
        Storage.SaveInBackground(FileName, _events.ToList());
        Count(_events[0]);
        Storage.SaveInBackground(StatsFile, _stats.Snapshot());
    }

    /// <summary>Clears what Home learns from. The all-time totals stay (they're just numbers).</summary>
    public void Clear()
    {
        _events.Clear();
        Storage.SaveInBackground(FileName, _events.ToList());
    }
}

/// <summary>How much you've listened to with Yesify, all time.</summary>
public sealed class ListeningStats
{
    public DateTime Since { get; set; }
    public double Seconds { get; set; }
    public int Plays { get; set; }
    public HashSet<string> Songs { get; set; } = [];

    public ListeningStats Snapshot() => new() { Since = Since, Seconds = Seconds, Plays = Plays, Songs = [.. Songs] };
}
