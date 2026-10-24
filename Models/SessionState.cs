namespace Yesify.Models;

/// <summary>
/// The playback session, saved so reopening Yesify continues where it left off: the list playing from and
/// its (possibly shuffled) order, how far into it we are, Up next, Recently played, and the current song.
/// Keeping the shuffle order means a reopened shuffle doesn't start over and repeat songs.
/// </summary>
public sealed class SessionState
{
    public List<Track> Context { get; set; } = [];
    public List<int> Order { get; set; } = [];
    public int OrderPos { get; set; } = -1;
    public List<Track> UpNext { get; set; } = [];
    public List<Track> History { get; set; } = [];
    public Track? Current { get; set; }
    public double PositionSeconds { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; }
}
