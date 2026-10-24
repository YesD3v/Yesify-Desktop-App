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
    public static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yesify");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

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
