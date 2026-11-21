<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="Assets/banner.png">
    <source media="(prefers-color-scheme: light)" srcset="Assets/banner-light.png">
    <img src="Assets/banner.png" width="560" alt="Yesify">
  </picture>
</p>

<p align="center"><b>A bold, native music player for Windows.</b><br>All of YouTube Music and your Spotify playlists. No account, no ads, unlimited skips.</p>

<p align="center"><a href="https://github.com/YesD3v/Yesify-Desktop-App/releases/latest/download/Yesify.exe"><b>Download Yesify.exe</b></a> &nbsp;·&nbsp; <a href="https://yesd3v.github.io/yesify-desktop/">Website</a></p>

## About

A small, native Windows music player (C# / WPF, no WebView, no Electron).
Paste a Spotify link and it plays, with the audio coming from YouTube.
No account, no Premium, no ads, no skip limits, no telemetry, no obfuscated code.

## Features

- **A bold glass look**: a vivid backdrop in the colors of the cover playing, rounded glass panels, a
  floating player bar, thick icons and heavy type. Album and playlist stats (year, tracks, length) sit in
  their own badges.
- **Library**: Liked Songs, your own playlists, saved ones, and **every album your saved songs come from**,
  as a grid or a list. Filter chips (All, Playlists, Albums), a search that also finds your songs,
  **Create playlist** and **Import** buttons, and **pins** to keep favorites on top. On an album's page, the
  songs you haven't saved are grayed out; an album that can't be found on YouTube Music shows the songs
  you saved from it. The sidebar collapses to an icon strip.
- **Cinema**: the whole window becomes the song, with a big cover, the title and controls, and huge
  lyrics that fade into the background (Esc to leave).
- **Background settings**: the album cover or your own colors (with presets), tint, brightness, and an
  optional slow motion with a frame-rate cap (5 to 120 FPS).
- **Import playlist**: paste a public Spotify playlist, album or track link, or a YouTube video or
  playlist link.
- **Your own playlists** with a title, description, author and cover picture. Right-click any song to
  add it to one. Imported playlists can be edited too (they become your own copy).
- **Search** your library instantly, and all of YouTube Music as you type: songs, artists, albums and
  playlists. Play any of them straight away.
- **YouTube Music browsing**, no account: artist pages (top songs, all songs, albums, singles & EPs,
  videos, live performances, playlists, "Fans might also like", and an endless **Mix**), album pages,
  playlists of any length, and **Explore** (new albums & singles, trending, charts, top artists, moods &
  genres). Save albums and playlists to your library; play, queue or add a whole artist, album or playlist
  to one of your playlists from its right-click menu.
- **Start Radio** from any song, **Go to Artist / Go to Album** from any song (also by clicking the names
  in song lists and the player bar), and **Back** (title bar or the mouse's back button).
- **Loading that feels instant**: every page shows glass placeholders with a shine sweeping across the
  moment you click, and is built behind them, so opening a page never stutters. Long lists only build
  the rows on screen.
- **Queue**: a resizable side panel (shared with Lyrics) with Up next and Recently played. Drag songs
  to reorder. The queue is saved between sessions.
- **Right-click anything**: songs, playlists, albums, mixes and artists (on Home, in the Library, in
  the queue and in the player bar) all have Play next, Add to queue (in order or shuffled) and Add to
  playlist.
- **Shuffle that remembers**: songs you haven't heard lately come first, so a big playlist doesn't
  keep repeating the same songs.
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
- **Taskbar buttons**: Previous, Play/Pause, Next and Mute under the window preview when you hover
  Yesify in the taskbar.
- Media keys, Space to play/pause, Ctrl+←/→ to skip.
- Explicit songs play uncensored: YouTube Music marks explicit uploads, and Yesify picks the version
  that matches Spotify's.
- **Fast starts**: finding a song's audio on YouTube (the slow part, 2.5 to 4 s) starts ahead of time,
  for the next song as soon as one starts and for any song the pointer rests on, so clicks play almost
  at once.
- The next song is pre-downloaded, so skips are near-instant. Downloaded audio is kept up to a size you
  pick in Settings (100 MB to 50 GB); the songs you play most are kept longest.

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
| `settings.json` | Settings (including the background and pinned tiles) and the playlists/albums imported into your library |
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
| `Icon.cs` | The app's bold, rounded vector icons (and the taskbar button pictures) |
| `AsyncImage.cs` | Cover loading off the UI thread, a disk cache, and a size-capped memory cache |
| `ViewModels/MainViewModel*.cs` | Playback, queue, shuffle and repeat, library, playlists, settings |

Note: streaming audio from YouTube outside YouTube's own apps goes against YouTube's Terms of Service.
If YouTube changes something and playback breaks, update the YoutubeExplode package first.
