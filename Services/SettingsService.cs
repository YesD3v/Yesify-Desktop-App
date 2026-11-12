using System.IO;
using System.Text.Json;
using Yesify.Models;

namespace Yesify.Services;

public sealed class Settings
{
    public double Volume { get; set; } = 0.8;
    public bool ShowLyrics { get; set; } = true;
    public bool ShowQueue { get; set; }
    public double RightPanelWidth { get; set; } = 340;
    // Lightweight: flat dark UI drawn in software (least memory). Otherwise the glass look.
    public bool Lightweight { get; set; }
    // Show what's playing on Discord (Rich Presence).
    public bool DiscordRpc { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public bool SidebarCollapsed { get; set; }
    // Miniplayer position and size, in device-independent pixels.
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }
    public double MiniWidth { get; set; } = 320;
    public double MiniHeight { get; set; } = 320;
    // Playlists / albums the user pinned to the sidebar from pasted links.
    public List<PlaylistInfo> SavedCollections { get; set; } = [];
}

/// <summary>Small JSON files under %APPDATA%\Yesify, written atomically so a crash can't corrupt them.</summary>
public static class Storage
{
    // Tests point YESIFY_DATA_DIR at a scratch copy, so they never touch the real library or audio cache.
    private static readonly string? Override =
        Environment.GetEnvironmentVariable("YESIFY_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    public static readonly string AppDataDir =
        Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yesify");

    /// <summary>Things that can always be downloaded again (audio, cover images).</summary>
    public static readonly string LocalDir = Override != null
        ? Path.Combine(Override, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yesify");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Writes a file on a background thread, in the order asked. For the big files that are saved while
    /// music plays (session, listening history), so the window never waits on the disk. The value must not
    /// be changed afterwards by the caller: pass a snapshot.
    /// </summary>
    public static void SaveInBackground<T>(string name, T snapshot)
    {
        lock (WriteLock)
            _writes = _writes.ContinueWith(_ =>
            {
                try { Save(name, snapshot); } catch { /* best effort, like the callers' own try/catch */ }
            }, TaskScheduler.Default);
    }

    /// <summary>Waits for background saves to finish (on exit).</summary>
    public static void Flush()
    {
        Task pending;
        lock (WriteLock) pending = _writes;
        try { pending.Wait(TimeSpan.FromSeconds(5)); } catch { /* exiting anyway */ }
    }

    private static readonly object WriteLock = new();
    private static Task _writes = Task.CompletedTask;

    public static T Load<T>(string name) where T : new()
    {
        var path = Path.Combine(AppDataDir, name);
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch
        {
            // Keep the unreadable file around instead of silently overwriting it later.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
        }
        return new T();
    }

    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(AppDataDir);
        var path = Path.Combine(AppDataDir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
