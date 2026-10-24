using System.Net.Http;
using System.Text.Json.Nodes;
using Yesify.Models;

namespace Yesify.Services;

/// <summary>
/// Fills in missing track covers from Spotify's public oEmbed endpoint (no login), caching the
/// results in covers.json so a playlist only has to be looked up once.
/// </summary>
public sealed class CoverService
{
    private const string FileName = "covers.json";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, string> _cache = Storage.Load<Dictionary<string, string>>(FileName);
    private readonly SemaphoreSlim _gate = new(6);
    private bool _dirty;

    /// <summary>Must be called from the UI thread; covers appear on the tracks as they arrive.</summary>
    public async Task FillAsync(IEnumerable<Track> tracks, CancellationToken ct)
    {
        var missing = new List<Track>();
        foreach (var t in tracks.Where(t => t.ImageUrl == null && t.SpotifyId != null))
        {
            if (_cache.TryGetValue(t.SpotifyId!, out var url)) t.ImageUrl = url;
            else missing.Add(t);
        }

        await Task.WhenAll(missing.Select(t => FetchAsync(t, ct)));

        if (_dirty)
        {
            _dirty = false;
            try { Storage.Save(FileName, _cache); } catch { /* cache only */ }
        }
    }

    private async Task FetchAsync(Track track, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct);
            string? url;
            try
            {
                var json = await _http.GetStringAsync(
                    $"https://open.spotify.com/oembed?url=https://open.spotify.com/track/{track.SpotifyId}", ct);
                url = (string?)JsonNode.Parse(json)?["thumbnail_url"];
            }
            finally
            {
                _gate.Release();
            }

            if (url == null) return;
            _cache[track.SpotifyId!] = url;
            _dirty = true;
            track.ImageUrl = url;
        }
        catch { /* a missing cover isn't worth an error */ }
    }
}
