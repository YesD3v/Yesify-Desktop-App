using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using Yesify.Models;

namespace Yesify.ViewModels;

/// <summary>An ObservableCollection that can swap all its items with one Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// The library knows its songs' albums: every album a liked / playlist / saved-playlist song comes from shows
/// up in Library (Albums), and on an album's page the songs you haven't saved anywhere are grayed out.
/// The Library search also finds your saved songs.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Songs in your library matching the Library search (shown under the tiles).</summary>
    public ObservableCollection<Track> LibrarySongs { get; } = [];

    private static string NormTitle(string s)
    {
        s = Regex.Replace(s.ToLowerInvariant(), @"\s*[\(\[].*?[\)\]]", ""); // (feat. …), [Remastered]
        var dash = s.IndexOf(" - ", StringComparison.Ordinal);                // "Song - Remastered 2011"
        if (dash > 0) s = s[..dash];
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static string NormAlbum(string s) => NormTitle(s);

    private static readonly char[] ArtistSeparators = [',', '&'];

    private static IEnumerable<string> ArtistNames(Track t) =>
        t.Artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries).Select(NormTitle).Where(a => a.Length > 0);

    /// <summary>
    /// Every album behind a saved song, most-saved first. Songs group by album name, and within a name by
    /// shared artists: "VULTURES 1" credited to "¥$, Kanye West" and to "Kanye West" is one album, while two
    /// unrelated albums that happen to share a name stay apart.
    /// </summary>
    private IEnumerable<LibraryItem> LibraryAlbums()
    {
        var savedNames = _settings.SavedCollections.Select(c => NormAlbum(c.Name)).ToHashSet();
        var albums = new List<List<Track>>();
        foreach (var byName in _library.EveryTrack(_liked.Tracks).Where(HasAlbum).GroupBy(t => NormAlbum(t.Album)))
        {
            if (byName.Key.Length == 0 || savedNames.Contains(byName.Key)) continue;
            // Clusters of songs connected by a shared artist.
            var clusters = new List<(HashSet<string> Artists, List<Track> Songs)>();
            foreach (var t in byName)
            {
                var names = ArtistNames(t).ToHashSet();
                var hits = clusters.Where(c => c.Artists.Overlaps(names)).ToList();
                if (hits.Count == 0) { clusters.Add((names, [t])); continue; }
                var into = hits[0];
                into.Artists.UnionWith(names);
                into.Songs.Add(t);
                foreach (var other in hits.Skip(1))
                {
                    into.Artists.UnionWith(other.Artists);
                    into.Songs.AddRange(other.Songs);
                    clusters.Remove(other);
                }
            }
            albums.AddRange(clusters.Select(c => c.Songs));
        }

        int Count(List<Track> g) => g.Select(t => NormTitle(t.Title)).Distinct().Count();
        return albums
            .OrderByDescending(Count)
            .ThenBy(g => g[0].Album, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                // Prefer a song that knows the album's YouTube Music page (opens straight away), and one with a cover.
                var rep = g.FirstOrDefault(t => t.AlbumId != null && t.ImageUrl != null) ?? g.FirstOrDefault(t => t.AlbumId != null)
                          ?? g.FirstOrDefault(t => t.ImageUrl != null) ?? g[0];
                // The plainest spelling of the name ("Graduation" over "Graduation (Deluxe Edition - Explicit)"),
                // and the artist most of its songs are credited to first.
                var title = g.Select(t => t.Album).Where(a => a.Length > 0).OrderBy(a => a.Length).FirstOrDefault() ?? rep.Album;
                var artist = g.GroupBy(t => t.PrimaryArtist).OrderByDescending(a => a.Count()).First().Key;
                return new LibraryItem(LibraryItemKind.Album, title, $"Album · {artist} · {Count(g)} saved",
                    g.Select(t => t.ImageUrl).FirstOrDefault(u => u != null), "album:" + NormAlbum(title) + "|" + NormTitle(artist), AlbumTrack: rep)
                {
                    AlbumSongs = g.GroupBy(t => NormTitle(t.Title)).Select(s => s.FirstOrDefault(t => t.ImageUrl != null) ?? s.First()).ToList(),
                };
            });
    }

    /// <summary>
    /// Grays out the songs of an album page that aren't in your library (liked, your playlists, saved
    /// playlists). A song counts as saved when a saved copy has the same title and the same main artist or
    /// album (saved copies often lack the album name). Nothing is grayed when none of the album is saved,
    /// or when you saved the album itself.
    /// </summary>
    private void DimUnsaved(PlaylistInfo album, string title, List<Track> tracks)
    {
        if (_settings.SavedCollections.Any(s => s.Id == album.Id)) return;
        var name = NormAlbum(title.Length > 0 ? title : album.Name);
        var byTitle = _library.EveryTrack(_liked.Tracks).ToLookup(t => NormTitle(t.Title));
        bool Saved(Track t) =>
            t.IsLiked || byTitle[NormTitle(t.Title)].Any(s =>
                (t.YouTubeId != null && s.YouTubeId == t.YouTubeId)
                || NormTitle(s.PrimaryArtist) == NormTitle(t.PrimaryArtist)
                || (s.Album.Length > 0 && NormAlbum(s.Album) == name));
        var saved = tracks.Select(Saved).ToList();
        if (!saved.Contains(true)) return;
        for (var i = 0; i < tracks.Count; i++) tracks[i].IsDimmed = !saved[i];
    }

    private void RefreshLibrarySongs(string query)
    {
        LibrarySongs.Clear();
        if (query.Length == 0) return;
        foreach (var t in _library.AllTracks(_liked.Tracks)
                     .Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                                 || t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                                 || t.Album.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .Take(60))
        {
            t.IsLiked = _liked.Contains(t);
            LibrarySongs.Add(t);
        }
    }

    /// <summary>Plays a song from the Library search, then on through the other songs found.</summary>
    [RelayCommand]
    private void PlayLibrarySong(Track? track)
    {
        if (track == null) return;
        var list = LibrarySongs.ToList();
        StartContext(list, Math.Max(0, list.IndexOf(track)));
    }
}
