using CommunityToolkit.Mvvm.ComponentModel;
using Yesify.Models;

namespace Yesify.ViewModels;

/// <summary>
/// Opening an album from Library: the page opens at once with what's known (cover, name, artist) and only
/// the song list shows a loading placeholder while the album is found on YouTube Music. If it isn't there
/// (an unreleased album, say), the page keeps your saved songs and a warning mark next to the title.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The songs of the open page are still on their way (the header is already shown).</summary>
    [ObservableProperty] private bool _isLoadingSongs;

    /// <summary>The open album wasn't found on YouTube Music; the page shows your saved songs from it.</summary>
    [ObservableProperty] private bool _viewNotFound;

    /// <summary>Starts finding a song's audio in the background (the pointer is resting on it).</summary>
    public void WarmTrack(Track? track) => _youtube.Warm(track);

    private Task OpenLibraryAlbumAsync(LibraryItem item)
    {
        Visit("libalbum:" + item.Id, () => OpenLibraryAlbumAsync(item));
        return LoadLibraryAlbumAsync(item);
    }

    private async Task LoadLibraryAlbumAsync(LibraryItem item)
    {
        SelectedPlaylist = null;
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        var ct = cts.Token;
        IsLoading = false;
        CurrentCollection = null;
        CurrentUserPlaylist = null;
        ViewDescription = "";
        _viewIsLiked = false;
        ViewIsLikedHeader = false;
        Tracks.Clear();
        ResetViewExtras();
        var artist = item.AlbumTrack?.PrimaryArtist ?? "";
        ViewTitle = item.Title;
        ViewSubtitle = artist.Length > 0 ? $"{artist} · Album" : "Album";
        ViewImageUrl = item.Image;
        ViewNotFound = false;
        IsLoadingSongs = true;
        CurrentView = ViewMode.Collection;

        List<Track> saved = (item.AlbumSongs ?? []).ToList();
        try
        {
            var id = item.AlbumTrack?.AlbumId;
            if (id == null) id = (await _music.FindAlbumAsync(item.Title, artist, ct))?.Id;
            ct.ThrowIfCancellationRequested();

            List<Track> tracks;
            if (id != null)
            {
                var page = await _music.AlbumAsync(id, ct);
                ct.ThrowIfCancellationRequested();
                CurrentCollection = page.Collection;
                ViewDescription = page.Description;
                if (page.Title.Length > 0) ViewTitle = page.Title;
                if (page.ImageUrl != null) ViewImageUrl = page.ImageUrl;
                foreach (var s in page.Sections.Select(Trim).Take(2)) ViewSections.Add(s);
                if (page.Sections.Count > 2) _ = AddRowsLaterAsync(page.Sections.Select(Trim).Skip(2).ToList(), ct);
                tracks = page.Songs;
                if (page.Subtitle.Length > 0) ViewSubtitle = page.Subtitle;
            }
            else
            {
                ViewNotFound = true;
                tracks = saved;
            }
            Show(tracks, dim: id != null);
            await _covers.FillAsync(tracks, ct);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Offline or YouTube Music didn't answer: still show what you saved.
            if (!ct.IsCancellationRequested)
            {
                ViewNotFound = true;
                Show(saved, dim: false);
            }
        }
        finally
        {
            if (_viewCts == cts) IsLoadingSongs = false;
        }

        void Show(List<Track> tracks, bool dim)
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                tracks[i].Number = i + 1;
                tracks[i].IsLiked = _liked.Contains(tracks[i]);
                tracks[i].IsDimmed = false;
            }
            if (dim && CurrentCollection != null) DimUnsaved(CurrentCollection, ViewTitle, tracks);
            Tracks.ReplaceAll(tracks);
            IsLoadingSongs = false;
            if (tracks.Count > 0) _youtube.Warm(tracks[0]);
        }
    }

    // ================================================================ Settings: Background → Motion speed

    /// <summary>Frames per second for the moving background (5–120).</summary>
    public double BackdropMotionFps
    {
        get => _settings.BackdropMotionFps;
        set => SetBackdrop(Math.Round(Math.Clamp(value, 5, 120)), _settings.BackdropMotionFps, v => _settings.BackdropMotionFps = v, nameof(BackdropMotionFps));
    }
}
