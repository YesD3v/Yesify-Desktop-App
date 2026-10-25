using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yesify.Models;

namespace Yesify.Services;

/// <summary>
/// Synced lyrics, matched to the exact length of the audio being played (a different version, like a
/// radio edit or a video with an intro, has different timing):
/// <list type="number">
/// <item>NetEase Cloud Music's public lyrics, which time every word ("yrc"), for a perfect word-by-word sweep;</item>
/// <item>Lyrica (https://codeberg.org/willooper/Lyrica), an open API over LRCLIB, YouTube Music, Musixmatch and
/// more, timed per line (word timing is then estimated);</item>
/// <item>unsynced lyrics as a last resort.</item>
/// </list>
/// </summary>
public sealed partial class LyricsService
{
    /// <summary>How far a lyrics version's length may be from the audio's and still line up.</summary>
    private static readonly TimeSpan LengthTolerance = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public LyricsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Yesify/1.0");
    }

    /// <param name="audioDuration">Length of the actual audio file; lyrics must match it to be in sync.</param>
    public async Task<List<LyricLine>> GetAsync(Track track, TimeSpan audioDuration, CancellationToken ct)
    {
        var duration = audioDuration > TimeSpan.Zero ? audioDuration : track.Duration;

        // Ask both at once; either may be down or have nothing.
        var netEaseTask = Quietly(() => GetNetEaseAsync(track, duration, ct), (null, null, TimeSpan.MaxValue));
        var lyricaTask = Quietly(() => GetLyricaAsync(track, duration, ct), []);
        await Task.WhenAll(netEaseTask, lyricaTask);
        ct.ThrowIfCancellationRequested();
        var (words, lines, lengthDiff) = netEaseTask.Result;
        var lyrica = lyricaTask.Result;
        var lyricaSynced = lyrica.Any(l => l.Time != null);

        // Word timing is unforgiving: only use it when NetEase's version is the same length as our audio
        // (within 1 s).
        if (words is { Count: > 0 } && lengthDiff <= TimeSpan.FromSeconds(1)) return words;

        if (lyricaSynced) return lyrica;
        if (lines is { Count: > 0 }) return lines; // line-timed beats unsynced
        return lyrica;
    }

    private static async Task<T> Quietly<T>(Func<Task<T>> work, T fallback)
    {
        try { return await work(); }
        catch (OperationCanceledException) { throw; }
        catch { return fallback; }
    }

    // ---------------------------------------------------------------- Lyrica

    // Public instances of Lyrica (https://codeberg.org/willooper/Lyrica), an open lyrics API that asks
    // LRCLIB, NetEase, YouTube Music, Musixmatch and others. The second is only tried if the first is down.
    private static readonly string[] LyricaHosts = ["https://wilooper-lyrica.hf.space", "https://test-0k.onrender.com"];

    // A song nobody has lyrics for can keep Lyrica searching for a minute: give up long before that.
    private static readonly TimeSpan LyricaTimeout = TimeSpan.FromSeconds(10);

    private async Task<List<LyricLine>> GetLyricaAsync(Track track, TimeSpan duration, CancellationToken ct)
    {
        var query = $"/lyrics/?artist={Uri.EscapeDataString(track.PrimaryArtist)}&song={Uri.EscapeDataString(CleanTitle(track.Title))}&timestamps=true&fast=true";
        JsonNode? data = null;
        foreach (var host in LyricaHosts)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LyricaTimeout);
            try
            {
                using var resp = await _http.GetAsync(host + query, timeout.Token);
                if ((int)resp.StatusCode >= 500) continue; // that instance is down
                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(timeout.Token));
                if ((string?)json?["status"] == "success") data = json?["data"];
                break; // "no lyrics" is an answer: the other instance would say the same
            }
            catch (HttpRequestException) { } // unreachable: try the other one
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // still searching: nothing found
        }
        if (data == null || (bool?)data["instrumental"] == true) return [];

        // Only trust the timing of a version as long as the audio (a radio edit or intro would drift).
        var fits = duration <= TimeSpan.Zero || (double?)data["duration"] is not { } d || d <= 0 ||
                   Math.Abs(d - duration.TotalSeconds) <= LengthTolerance.TotalSeconds;

        var timed = (data["timed_lyrics"]?.AsArray() ?? []).OfType<JsonNode>()
            .Select(l => (Ms: (double?)l["start_time"] ?? -1, Text: ((string?)l["text"] ?? "").Trim()))
            .Where(l => l.Ms >= 0)
            .ToList();
        // NetEase's credit lines ("作词 : ...", "Samples: ...") come first, stamped a few milliseconds apart.
        var skip = 0;
        while (skip + 1 < timed.Count && timed[skip].Ms < 3000 && (timed[skip + 1].Ms - timed[skip].Ms < 300 || CreditLine().IsMatch(timed[skip].Text))) skip++;
        timed = timed.Skip(skip).Where(l => !CreditLine().IsMatch(l.Text)).ToList();

        if ((bool?)data["hasTimestamps"] == true && fits && timed.Count > 0)
            return ParseLrc(string.Join('\n', timed.Select(l =>
                string.Create(CultureInfo.InvariantCulture, $"[{(int)(l.Ms / 60000)}:{l.Ms % 60000 / 1000:00.000}]{l.Text}"))));

        // Unsynced (or a different length): the words without timing.
        var plain = timed.Count > 0 ? timed.Select(l => l.Text) : ((string?)data["lyrics"] ?? "").Split('\n').Select(l => LrcLine().Match(l.Trim()) is { Success: true } m ? m.Groups[3].Value.Trim() : l.Trim());
        return plain.Where(l => !CreditLine().IsMatch(l)).Select(Plain).ToList();
    }

    // ---------------------------------------------------------------- NetEase (word-timed)

    /// <returns>Word-timed lines if NetEase has them, and/or its line-timed lyrics, for a version matching the audio.</returns>
    private async Task<(List<LyricLine>? Words, List<LyricLine>? Lines, TimeSpan LengthDiff)> GetNetEaseAsync(Track track, TimeSpan duration, CancellationToken ct)
    {
        var title = CleanTitle(track.Title);
        using var search = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/cloudsearch/pc")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = $"{track.PrimaryArtist} {title}", ["type"] = "1", ["limit"] = "10", ["offset"] = "0",
            }),
        };
        search.Headers.Referrer = new Uri("https://music.163.com");
        using var resp = await _http.SendAsync(search, ct);
        resp.EnsureSuccessStatusCode();
        var songs = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["result"]?["songs"]?.AsArray();
        if (songs == null) return (null, null, TimeSpan.MaxValue);

        // Same title, same artist, and (crucially) the same length as the audio we're playing.
        var want = Normalize(title);
        var artists = track.Artist.Split(',').Select(a => Normalize(a)).Where(a => a.Length > 0).ToList();
        var match = songs.OfType<JsonNode>()
            .Select(s => (Song: s, Name: Normalize(CleanTitle((string?)s["name"] ?? "")), Ms: (double?)s["dt"] ?? 0,
                          Artists: (s["ar"]?.AsArray() ?? []).Select(a => Normalize((string?)a?["name"] ?? "")).ToList()))
            .Where(x => x.Name == want && x.Artists.Any(a => artists.Any(w => a.Contains(w) || w.Contains(a))))
            .Where(x => duration <= TimeSpan.Zero || Math.Abs(x.Ms - duration.TotalMilliseconds) <= LengthTolerance.TotalMilliseconds)
            .OrderBy(x => Math.Abs(x.Ms - duration.TotalMilliseconds))
            .FirstOrDefault();
        if (match.Song == null) return (null, null, TimeSpan.MaxValue);

        var id = (long?)match.Song["id"];
        using var lyricReq = new HttpRequestMessage(HttpMethod.Get,
            $"https://music.163.com/api/song/lyric/v1?id={id}&cp=false&tv=0&lv=0&rv=0&kv=0&yv=0&ytv=0&yrv=0");
        lyricReq.Headers.Referrer = new Uri("https://music.163.com");
        using var lyricResp = await _http.SendAsync(lyricReq, ct);
        lyricResp.EnsureSuccessStatusCode();
        var lyric = JsonNode.Parse(await lyricResp.Content.ReadAsStringAsync(ct));

        var lrc = (string?)lyric?["lrc"]?["lyric"];
        var words = (string?)lyric?["yrc"]?["lyric"] is { Length: > 0 } yrc ? ParseYrc(yrc, lrc) : null;
        var lines = lrc is { Length: > 0 } ? ParseLrc(StripCredits(lrc)) : null;
        var diff = duration > TimeSpan.Zero ? TimeSpan.FromMilliseconds(Math.Abs(match.Ms - duration.TotalMilliseconds)) : TimeSpan.Zero;
        return (words, lines, diff);
    }

    private static string StripCredits(string lrc) =>
        string.Join('\n', lrc.Split('\n').Where(l => !CreditLine().IsMatch(LrcLine().Match(l.Trim()) is { Success: true } m ? m.Groups[3].Value : "")));

    // Credit lines NetEase adds ("作词 : ...", "Producer: ...").
    [GeneratedRegex(@"^\s*(作词|作曲|编曲|制作人|制作|混音|母带|和声|吉他|贝斯|鼓|弦乐|录音|监制|出品|Lyricist|Composer|Producer|Co-producers?|Arranger|Writers?|Samples?|Written by|Lyrics by|Music by)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex CreditLine();

    // "[lineStartMs,lineDurMs](wordStartMs,wordDurMs,0)word (…)word…"
    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")]
    private static partial Regex YrcLine();

    [GeneratedRegex(@"\((\d+),(\d+),-?\d+\)")]
    private static partial Regex YrcWord();

    /// <param name="lrc">NetEase's line-timed text for the same song; used to restore spaces the word data drops.</param>
    private static List<LyricLine> ParseYrc(string yrc, string? lrc)
    {
        var reference = lrc == null ? [] : ParseLrc(lrc).Where(l => l.Time != null && !l.IsBreak).ToList();
        var timed = new List<(TimeSpan Start, TimeSpan End, string Text, List<LyricWord> Words)>();
        foreach (var raw in yrc.Split('\n'))
        {
            var m = YrcLine().Match(raw.Trim());
            if (!m.Success) continue; // JSON metadata lines
            var lineStart = TimeSpan.FromMilliseconds(long.Parse(m.Groups[1].Value));
            var lineEnd = lineStart + TimeSpan.FromMilliseconds(long.Parse(m.Groups[2].Value));
            var body = m.Groups[3].Value;

            var words = new List<LyricWord>();
            var stamps = YrcWord().Matches(body);
            for (var i = 0; i < stamps.Count; i++)
            {
                var from = stamps[i].Index + stamps[i].Length;
                var to = i + 1 < stamps.Count ? stamps[i + 1].Index : body.Length;
                var text = body[from..to];
                if (text.Length == 0) continue;
                var start = TimeSpan.FromMilliseconds(long.Parse(stamps[i].Groups[1].Value));
                words.Add(new LyricWord(text, start, start + TimeSpan.FromMilliseconds(long.Parse(stamps[i].Groups[2].Value))));
            }
            var same = reference.FirstOrDefault(r => Math.Abs((r.Time!.Value - lineStart).TotalMilliseconds) < 1500);
            if (same != null) words = RestoreSpaces(words, same.Text);
            var lineText = string.Concat(words.Select(w => w.Text)).Trim();
            if (lineText.Length == 0 || CreditLine().IsMatch(lineText)) continue;
            timed.Add((lineStart, lineEnd, lineText, words));
        }
        timed.Sort((a, b) => a.Start.CompareTo(b.Start));

        var lines = new List<LyricLine>();
        if (timed.Count > 0 && timed[0].Start > TimeSpan.FromSeconds(4))
            lines.Add(new LyricLine(TimeSpan.Zero, "") { IsBreak = true, End = timed[0].Start }); // intro
        for (var i = 0; i < timed.Count; i++)
        {
            var (start, end, text, words) = timed[i];
            lines.Add(new LyricLine(start, text) { End = end, Words = words, IsWordTimed = true });
            // A long gap before the next line is an instrumental break.
            if (i + 1 < timed.Count && timed[i + 1].Start - end > TimeSpan.FromSeconds(5))
                lines.Add(new LyricLine(end + TimeSpan.FromMilliseconds(500), "") { IsBreak = true, End = timed[i + 1].Start });
        }
        return lines;
    }

    /// <summary>
    /// The word data sometimes glues words together ("how" + "to " → "howto") but also legitimately splits
    /// words into syllables. Walk the properly spaced line text alongside the words and take its spacing.
    /// If they don't line up, keep the words as they were.
    /// </summary>
    private static List<LyricWord> RestoreSpaces(List<LyricWord> words, string text)
    {
        var result = new List<LyricWord>(words.Count);
        var p = 0;
        foreach (var w in words)
        {
            var core = w.Text.Trim();
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            if (string.Compare(text, p, core, 0, core.Length, StringComparison.OrdinalIgnoreCase) != 0) return words;
            p += core.Length;
            var spaced = p < text.Length && char.IsWhiteSpace(text[p]);
            result.Add(w with { Text = spaced ? core + " " : core });
        }
        return result;
    }

    // "Song (feat. X) - Remastered 2011" -> "Song"
    private static string CleanTitle(string title)
    {
        var t = title;
        var dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) t = t[..dash];
        var paren = t.IndexOfAny(['(', '[']);
        if (paren > 0) t = t[..paren];
        return t.Trim();
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ---------------------------------------------------------------- LRC
    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$")]
    private static partial Regex LrcLine();

    // Enhanced LRC word stamps: "<00:12.34>word"
    [GeneratedRegex(@"<(\d+):(\d+(?:\.\d+)?)>")]
    private static partial Regex WordStamp();

    private static TimeSpan Stamp(string min, string sec) =>
        TimeSpan.FromMinutes(int.Parse(min)) + TimeSpan.FromSeconds(double.Parse(sec, CultureInfo.InvariantCulture));

    private static List<LyricLine> ParseLrc(string lrc)
    {
        // First pass: line times, text, and any per-word stamps.
        var raw = new List<(TimeSpan Time, string Text, List<(TimeSpan Start, string Word)>? Stamped)>();
        foreach (var line in lrc.Split('\n'))
        {
            var m = LrcLine().Match(line.Trim());
            if (!m.Success) continue;
            var time = Stamp(m.Groups[1].Value, m.Groups[2].Value);
            var body = m.Groups[3].Value;

            List<(TimeSpan, string)>? stamped = null;
            var stamps = WordStamp().Matches(body);
            if (stamps.Count > 0)
            {
                stamped = [];
                for (var i = 0; i < stamps.Count; i++)
                {
                    var start = stamps[i].Index + stamps[i].Length;
                    var end = i + 1 < stamps.Count ? stamps[i + 1].Index : body.Length;
                    var word = body[start..end];
                    if (word.Trim().Length > 0) stamped.Add((Stamp(stamps[i].Groups[1].Value, stamps[i].Groups[2].Value), word));
                }
                body = WordStamp().Replace(body, "");
            }
            raw.Add((time, body.Trim(), stamped));
        }

        // Second pass: end times, words, and instrumental breaks.
        var lines = new List<LyricLine>();
        if (raw.Count > 0 && raw[0].Time > TimeSpan.FromSeconds(4))
            lines.Add(new LyricLine(TimeSpan.Zero, "") { IsBreak = true, End = raw[0].Time }); // intro

        for (var i = 0; i < raw.Count; i++)
        {
            var (time, text, stamped) = raw[i];
            var next = i + 1 < raw.Count ? raw[i + 1].Time : time + TimeSpan.FromSeconds(6);
            // Empty lines and lines that are only music symbols ("♪") mark instrumental breaks.
            if (text.All(ch => char.IsWhiteSpace(ch) || "♪♫♬♩🎵🎶".Contains(ch)))
            {
                lines.Add(new LyricLine(time, "") { IsBreak = true, End = next });
                continue;
            }
            lines.Add(new LyricLine(time, text) { End = next, Words = stamped != null ? FromStamps(stamped, next) : Spread(text, time, next), IsWordTimed = stamped != null });
        }
        return lines;
    }

    private static List<LyricWord> FromStamps(List<(TimeSpan Start, string Word)> stamped, TimeSpan lineEnd)
    {
        var words = new List<LyricWord>();
        for (var i = 0; i < stamped.Count; i++)
        {
            var end = i + 1 < stamped.Count ? stamped[i + 1].Start : lineEnd;
            words.Add(new LyricWord(stamped[i].Word.TrimStart() + (stamped[i].Word.EndsWith(' ') ? "" : " "), stamped[i].Start, end));
        }
        return words;
    }

    /// <summary>
    /// Without per-word timing, estimate it: a line is sung over roughly its length (~75 ms per character),
    /// never past the next line, and each word gets a share proportional to its length.
    /// </summary>
    private static List<LyricWord> Spread(string text, TimeSpan start, TimeSpan next)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chars = parts.Sum(p => p.Length + 1);
        var gap = (next - start).TotalSeconds;
        var sung = Math.Min(gap * 0.95, Math.Max(1.2, chars * 0.075 + 0.4));
        var words = new List<LyricWord>();
        var t = start;
        foreach (var p in parts)
        {
            var d = TimeSpan.FromSeconds(sung * (p.Length + 1) / chars);
            words.Add(new LyricWord(p + " ", t, t + d));
            t += d;
        }
        return words;
    }

    /// <summary>Unsynced lyrics: words without timing (never animated).</summary>
    public static LyricLine Plain(string text) =>
        new(null, text) { Words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new LyricWord(w + " ", TimeSpan.Zero, TimeSpan.Zero)).ToList() };
}