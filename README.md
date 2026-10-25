# Yesify

A small, native Windows music player (C# / WPF, no WebView, no Electron).
Paste a Spotify link and it plays, with the audio coming from YouTube.
No account, no Premium, no ads, no skip limits, no telemetry, no obfuscated code.

## Features

- **Library**: Liked Songs, your own playlists and imported ones as a grid of covers. The sidebar
  collapses to an icon strip.
- **Import playlist**: paste a public Spotify playlist, album or track link, or a YouTube video or
  playlist link.
- **Your own playlists** with a title, description, author and cover picture. Right-click any song to
  add it to one. Imported playlists can be edited too (they become your own copy).
- **Search** across every song in your library.
- **Two looks**: Glass (the default: frosted panels over a blurred, vivid version of the artwork) and
  **Lightweight mode** (flat dark, drawn by the CPU, least memory). Switch it in Settings.
- **Queue**: a resizable side panel (shared with Lyrics) with Up next and Recently played. Drag songs
  to reorder. Right-click a song for Play next or Add to queue. The queue is saved between sessions.
- **Synced lyrics**, matched to the exact length of the audio playing: word-by-word timing from NetEase
  Cloud Music's public lyrics when available, otherwise line timing from [LRCLIB](https://lrclib.net).
  Click a line to jump to it.
- **Miniplayer**: a small always-on-top window with a gradient taken from the album cover. Resize it
  freely: wide puts the cover on the left, tall puts it on top.
- **Closing the window (X) keeps the music playing** from the tray icon. While closed, the UI is
  unloaded and memory is trimmed. `Yesify.exe --tray` starts straight into the tray.
- **Windows media controls**: the song, artist, cover and timeline show in the Windows media flyout and
  lock screen, and anything that reads Windows media sessions can control playback.
- **Discord Rich Presence** (on by default, in Settings): "Listening to Yesify" with the song, artist,
  album cover and progress.
- Media keys, Space to play/pause, Ctrl+←/→ to skip.
- Explicit songs play uncensored: YouTube Music marks explicit uploads, and Yesify picks the version
  that matches Spotify's.
- The next song is pre-downloaded, so skips are near-instant (cache capped at 1 GB).

Long playlists: the first 100 songs come from Spotify's public embed page, and the rest load in the
background through Spotify's player backend, using the anonymous token the embed page hands every
visitor. That backend is undocumented. If Spotify changes it, you'll get the first 100 songs instead of
an error.

## Build and run

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10 or 11.

```
dotnet run -c Release
```

Or double-click `Yesify.bat`, which runs the published exe if there is one and otherwise builds from
source.

To build the single portable exe that `Yesify.bat` launches (about 160 MB, no .NET install needed). It
is left uncompressed on purpose: Windows maps it from disk on demand, while a compressed exe would have
to unpack itself into RAM.

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish
```

`Yesify.exe "some song"` or `Yesify.exe <link>` opens straight to that search or link.

## Your data

Everything stays on your PC, in `%APPDATA%\Yesify`:

| File | Contents |
| --- | --- |
| `settings.json` | Settings and the playlists/albums imported into your library |
| `liked.json` | Liked Songs |
| `playlists.json` | Your own playlists (their pictures are in `covers\`) |
| `session.json` | The song, position and queue to pick up where you left off |
| `library-tracks.json` | Songs of imported playlists, for library search |
| `covers.json`, `spotify-tracks-v2.json`, `matches-v3.json` | Caches: cover URLs, track details, and which YouTube upload each song plays |

Writes go to a temp file first and are then swapped in, so a crash can't corrupt them. Audio is cached
in `%LOCALAPPDATA%\Yesify\cache`.

## How it works

| File | Role |
| --- | --- |
| `Services/SpotifyEmbedService.cs` | Reads track lists from Spotify's public embed page (`open.spotify.com/embed/...`) |
| `Services/CoverService.cs` | Per-song covers from Spotify's public oEmbed endpoint |
| `Services/YouTubeService.cs` | Matches each song to a YouTube Music upload (artist, title, duration, explicit version) and downloads the audio with [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) |
| `Services/Mp4Fixer.cs` | Removes an edit-list box that makes Windows decode some YouTube files as silence |
| `Services/AudioPlayer.cs` | NAudio + Windows Media Foundation playback, plus a smooth playback clock for lyrics |
| `Services/PreciseSeekReader.cs` | Sample-exact seeking (Media Foundation's own seeking lands up to 14 s off on these files) |
| `Services/LyricsService.cs` | NetEase word-timed lyrics, LRCLIB fallback, LRC parsing |
| `Services/LibraryService.cs` | Your playlists and the library search index |
| `Services/LikedService.cs` | Local Liked Songs |
| `Services/DiscordPresence.cs` | Discord Rich Presence over Discord's local pipe (no SDK) |
| `MediaSession.cs` | Windows media session (SystemMediaTransportControls) through plain COM calls |
| `LyricsAnimator.cs` | The Apple Music-style word sweep and the dots during instrumental breaks |
| `Frost.cs` | Frosted-glass pop-ups (blurs a live picture of the window behind them) |
| `ViewModels/MainViewModel*.cs` | Playback, queue, shuffle and repeat, library, playlists, settings |

Note: streaming audio from YouTube outside YouTube's own apps goes against YouTube's Terms of Service.
If YouTube changes something and playback breaks, update the YoutubeExplode package first.
