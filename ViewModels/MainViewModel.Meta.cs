using System.Text.RegularExpressions;

namespace Yesify.ViewModels;

/// <summary>
/// The stats under a playlist / album title as Liner-style badges: the year, the number of tracks and the
/// total length ("2007" · "14 tracks" · "54m"). The rest of the subtitle (artist, "Album", owner) stays as
/// the line under the title.
/// </summary>
public sealed partial class MainViewModel
{
    public string ViewSubtitleLine { get; private set; } = "";
    public string ViewMetaYear { get; private set; } = "";
    public string ViewMetaTracks { get; private set; } = "";
    public string ViewMetaDuration { get; private set; } = "";

    private static readonly Regex YearPart = new(@"^(19|20)\d\d$");
    private static readonly Regex CountPart = new(@"^([\d,]+) (songs?|tracks?)$", RegexOptions.IgnoreCase);
    private static readonly Regex DurationPart = new(@"^(?:(\d+)\s*hours?)?[,\s]*(?:(\d+)\s*minutes?)?$|^(\d+)\+? minutes?$", RegexOptions.IgnoreCase);

    partial void OnViewSubtitleChanged(string value) => RefreshViewMeta();

    private void RefreshViewMeta()
    {
        var keep = new List<string>();
        string year = "";
        var count = 0;
        var said = TimeSpan.Zero;
        foreach (var raw in (ViewSubtitle ?? "").Split(" · "))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            if (YearPart.IsMatch(part)) { year = part; continue; }
            if (CountPart.Match(part) is { Success: true } c) { int.TryParse(c.Groups[1].Value.Replace(",", ""), out count); continue; }
            if ((part.Contains("minute", StringComparison.OrdinalIgnoreCase) || part.Contains("hour", StringComparison.OrdinalIgnoreCase))
                && DurationPart.Match(part) is { Success: true } d)
            {
                int.TryParse(d.Groups[1].Value, out var h);
                int.TryParse(d.Groups[2].Success && d.Groups[2].Length > 0 ? d.Groups[2].Value : d.Groups[3].Value, out var m);
                said = new TimeSpan(h, m, 0);
                continue;
            }
            keep.Add(part);
        }

        var tracks = Math.Max(count, Tracks.Count);
        var sum = TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
        var total = sum > TimeSpan.Zero && Tracks.Count >= count ? sum : said;

        ViewSubtitleLine = string.Join(" · ", keep);
        ViewMetaYear = year;
        ViewMetaTracks = tracks == 0 ? "" : tracks == 1 ? "1 track" : $"{tracks:N0} tracks";
        ViewMetaDuration = total.TotalMinutes < 1 ? ""
            : total.TotalHours >= 1 ? $"{(int)total.TotalHours}h {total.Minutes}m" : $"{total.Minutes}m";
        OnPropertyChanged(nameof(ViewSubtitleLine));
        OnPropertyChanged(nameof(ViewMetaYear));
        OnPropertyChanged(nameof(ViewMetaTracks));
        OnPropertyChanged(nameof(ViewMetaDuration));
    }
}
