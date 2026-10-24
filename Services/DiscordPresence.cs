using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yesify.Models;
using Yesify.ViewModels;

namespace Yesify.Services;

/// <summary>
/// Discord Rich Presence: "Listening to Yesify" with the song, the artist, a progress bar, and the album
/// cover with Yesify's icon in its corner. Talks to the Discord app over its local pipe (no SDK). If
/// Discord isn't running it quietly retries now and then. While paused, or with the setting off, the
/// status is cleared.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    private const string ClientId = "1563571555647234158";
    private const string Icon = "https://cdn.discordapp.com/app-icons/1563571555647234158/b96915ea3390fd742d80473deda8c125.png?size=256";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(20);

    private readonly MainViewModel _vm;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly object _lock = new();
    private JsonNode? _activity;  // what Discord should show; null = nothing
    private bool _dirty;
    private Track? _track;

    private readonly string _pipePrefix;

    /// <param name="pipePrefix">Discord's pipe names (tests point this at a stand-in).</param>
    public DiscordPresence(MainViewModel vm, string pipePrefix = "discord-ipc-")
    {
        _vm = vm;
        _pipePrefix = pipePrefix;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Seeked += Refresh;
        WatchTrack(_vm.CurrentTrack);
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    // ---------------------------------------------------------------- following the player

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentTrack):
                WatchTrack(_vm.CurrentTrack);
                break;
            case nameof(MainViewModel.IsPlaying):
            case nameof(MainViewModel.DurationSeconds):
            case nameof(MainViewModel.ShowOnDiscord):
                Refresh();
                break;
        }
    }

    // Covers can arrive after the song starts (playlist covers load lazily), so follow ImageUrl too.
    private void WatchTrack(Track? track)
    {
        if (_track != null) _track.PropertyChanged -= OnTrackPropertyChanged;
        _track = track;
        if (track != null) track.PropertyChanged += OnTrackPropertyChanged;
        Refresh();
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Track.ImageUrl)) Refresh();
    }

    /// <summary>Works out what to show (on the UI thread) and hands it to the connection.</summary>
    private void Refresh()
    {
        var t = _track;
        JsonNode? activity = null;
        if (_vm.ShowOnDiscord && t != null && _vm.IsPlaying)
        {
            var duration = TimeSpan.FromSeconds(Math.Max(_vm.DurationSeconds > 1 ? _vm.DurationSeconds : 0, t.Duration.TotalSeconds));
            var start = DateTimeOffset.UtcNow - _vm.PlaybackPosition;
            var assets = new JsonObject
            {
                ["large_image"] = t.ImageUrl is { } img && img.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? img : Icon,
                ["small_image"] = Icon,
                ["small_text"] = "Yesify",
            };
            if (Text(t.Album) is { } album) assets["large_text"] = album;
            activity = new JsonObject
            {
                ["type"] = 2, // Listening
                ["details"] = Text(t.Title) ?? "Unknown song",
                ["state"] = Text(t.Artist) ?? "Unknown artist",
                ["assets"] = assets,
            };
            if (duration > TimeSpan.Zero)
                activity["timestamps"] = new JsonObject
                {
                    ["start"] = start.ToUnixTimeMilliseconds(),
                    ["end"] = (start + duration).ToUnixTimeMilliseconds(),
                };
        }

        lock (_lock)
        {
            if (JsonNode.DeepEquals(activity, _activity) && !_dirty) return;
            _activity = activity;
            _dirty = true;
        }
        if (_changed.CurrentCount == 0) try { _changed.Release(); } catch (SemaphoreFullException) { }
    }

    // Discord wants 2 to 128 characters.
    private static string? Text(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length == 1) s += " ";
        return s.Length > 128 ? s[..127] + "…" : s;
    }

    // ---------------------------------------------------------------- the connection

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Nothing to show and never connected: no need for Discord at all.
                await WaitForWorkAsync(ct);
                using var pipe = await ConnectAsync(ct);
                if (pipe == null)
                {
                    await WaitAsync(RetryDelay, ct); // Discord isn't running
                    continue;
                }
                await WriteAsync(pipe, 0, new JsonObject { ["v"] = 1, ["client_id"] = ClientId }, ct); // handshake
                await ReadAsync(pipe, ct); // READY
                lock (_lock) _dirty = true;

                while (!ct.IsCancellationRequested)
                {
                    JsonNode? activity;
                    bool dirty;
                    lock (_lock) { activity = _activity?.DeepClone(); dirty = _dirty; _dirty = false; }
                    if (dirty)
                    {
                        await WriteAsync(pipe, 1, new JsonObject
                        {
                            ["cmd"] = "SET_ACTIVITY",
                            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
                            ["nonce"] = Guid.NewGuid().ToString(),
                        }, ct);
                        await ReadAsync(pipe, ct);
                        await Task.Delay(1000, ct); // Discord allows a handful of updates per 20 seconds
                    }
                    await _changed.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Discord closed or the pipe broke: try again later.
                try { await WaitAsync(RetryDelay, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_lock) if (_activity != null) return;
            await _changed.WaitAsync(ct);
        }
    }

    // Sleeps, but wakes early when there's something new to show.
    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await _changed.WaitAsync(delay, ct); }
        finally { lock (_lock) _dirty = true; }
    }

    private async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"{_pipePrefix}{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150, ct);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
            }
        }
        return null;
    }

    // Frames: opcode (int32 LE), length (int32 LE), UTF-8 JSON.
    private static async Task WriteAsync(Stream pipe, int opcode, JsonNode payload, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, ct);
        await pipe.FlushAsync(ct);
    }

    private static async Task<JsonNode?> ReadAsync(Stream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, timeout.Token);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1 << 20) throw new IOException("Bad frame from Discord.");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, timeout.Token);
        if (opcode == 2) throw new IOException("Discord closed the connection."); // CLOSE
        return JsonNode.Parse(body);
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Seeked -= Refresh;
        if (_track != null) _track.PropertyChanged -= OnTrackPropertyChanged;
        _cts.Cancel(); // closing the pipe clears the status in Discord
    }
}
