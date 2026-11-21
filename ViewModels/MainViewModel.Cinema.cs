using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yesify.ViewModels;

/// <summary>Cinema mode (full-window now playing) and the Library page's filter chips, search and grid/list toggle.</summary>
public sealed partial class MainViewModel
{
    // ================================================================ cinema

    /// <summary>The whole window shows the cover, the song and big lyrics.</summary>
    [ObservableProperty] private bool _isCinema;

    [RelayCommand]
    private void ToggleCinema() => IsCinema = !IsCinema;

    // ================================================================ library toolbar

    /// <summary>"All", "Playlists" or "Albums".</summary>
    [ObservableProperty] private string _libraryFilter = "All";

    /// <summary>The Library page's own search field (filters the tiles by name).</summary>
    [ObservableProperty] private string _libraryFilterQuery = "";

    [ObservableProperty] private bool _libraryListMode;

    partial void OnLibraryFilterChanged(string value) => RefreshLibrary();
    partial void OnLibraryFilterQueryChanged(string value) => RefreshLibrary();

    [RelayCommand]
    private void SetLibraryFilter(string? filter) => LibraryFilter = filter ?? "All";

    [RelayCommand]
    private void SetLibraryListMode(string? mode) => LibraryListMode = mode == "list";
}
