using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yesify.Services;

namespace Yesify.ViewModels;

/// <summary>
/// Updates (see <see cref="Updater"/>): checked once per launch. When there's one, the update screen takes
/// the window (like the idle screen) to offer it; "Not now" leaves an Update button over Settings for the
/// rest of the session.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The new copy is in place and started: the app should save and quit.</summary>
    public event Action? RestartForUpdateRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton), nameof(UpdateChips), nameof(UpdateButtonTip))]
    private UpdateInfo? _availableUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    private bool _isUpdateScreenOpen;

    /// <summary>Downloading / building / installing: the progress replaces the buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateOffer), nameof(UpdateRingValue), nameof(UpdateNavText), nameof(UpdateButtonTip))]
    private bool _isUpdating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateNavText), nameof(UpdateButtonTip))]
    private string _updateStage = "";
    [ObservableProperty] private string _updateDetail = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateRingValue), nameof(UpdateNavText), nameof(UpdateButtonTip))]
    private double? _updateFraction;
    [ObservableProperty] private bool _canCancelUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateActionText))]
    private string? _updateError;

    private CancellationTokenSource? _updateCts;

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    public bool ShowUpdateButton => AvailableUpdate != null && !IsUpdateScreenOpen;
    public bool ShowUpdateOffer => !IsUpdating;
    public string UpdateActionText => UpdateError == null ? "Install update" : "Try again";
    public string UpdateButtonTip => IsUpdating ? $"{UpdateNavText}: click to see the details" :
        AvailableUpdate is { } u ? $"{u.Title} is ready to install" : "";

    /// <summary>The sidebar ring while installing: 0–1 filled, -1 when there's no telling (building, swapping).</summary>
    public double UpdateRingValue => IsUpdating ? UpdateFraction ?? -1 : 0;

    /// <summary>The sidebar entry's label: "Update", then how far along the install is.</summary>
    public string UpdateNavText => !IsUpdating ? "Update"
        : UpdateFraction is { } f && UpdateStage == "Downloading" ? $"Updating {f * 100:0}%"
        : UpdateStage is { Length: > 0 } s ? s : "Updating";

    /// <summary>Small facts under the title: when, how big, what you have now.</summary>
    public IReadOnlyList<string> UpdateChips
    {
        get
        {
            if (AvailableUpdate is not { } u) return [];
            var chips = new List<string> { (u.Kind == UpdateKind.Release ? "Released " : "Pushed ") + Ago(u.PublishedAt) };
            if (u.SizeBytes > 0) chips.Add(Updater.FormatMb(u.SizeBytes));
            if (Updater.Simulate) { chips.Add("Simulation"); return chips; }
            chips.Add(u.Kind == UpdateKind.Release
                ? $"Yours is from {u.LocalTime.ToLocalTime():MMM d}"
                : u.Kind == UpdateKind.SourceGit ? "Pull & rebuild" : "Built from source");
            return chips;
        }
    }

    /// <summary>Called by the app once at launch.</summary>
    public async Task CheckForUpdatesAsync()
    {
        try
        {
            // Out of the way of startup (session restore, Home), and never longer than it's worth.
            await Task.Delay(TimeSpan.FromSeconds(3));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await Updater.CheckAsync(cts.Token);
            if (update == null) return;
            AvailableUpdate = update;
            UpdateError = null;
            IsUpdateScreenOpen = true;
        }
        catch
        {
            // Offline, GitHub's rate limit, ...: try again next launch.
        }
    }

    /// <summary>For the test harness: shows the update screen with the given update.</summary>
    internal void OfferUpdate(UpdateInfo update)
    {
        AvailableUpdate = update;
        UpdateError = null;
        IsUpdateScreenOpen = true;
    }

    [RelayCommand]
    private void ShowUpdate()
    {
        if (AvailableUpdate == null) return;
        if (!IsUpdating) UpdateError = null;
        IsUpdateScreenOpen = true;
    }

    [RelayCommand]
    private void DismissUpdate() => IsUpdateScreenOpen = false; // while installing, it carries on in the sidebar

    [RelayCommand]
    private void CancelUpdate() => _updateCts?.Cancel();

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (AvailableUpdate is not { } update || IsUpdating) return;
        UpdateError = null;
        IsUpdating = true;
        // Keep the PC awake (not the screen) until the update is in place: sleeping mid-download or mid-build breaks it.
        SetThreadExecutionState(EsContinuous | EsSystemRequired);
        UpdateStage = "Getting ready";
        UpdateDetail = "";
        UpdateFraction = null;
        CanCancelUpdate = true;
        // Back to the app: the install carries on in the sidebar's Update entry (a ring filling up).
        IsUpdateScreenOpen = false;
        _updateCts = new CancellationTokenSource();
        var progress = new Progress<UpdateProgress>(p =>
        {
            if (!IsUpdating) return; // a late report after cancelling
            UpdateStage = p.Stage;
            UpdateDetail = p.Detail;
            UpdateFraction = p.Fraction;
            CanCancelUpdate = p.CanCancel;
        });
        try
        {
            await Updater.InstallAsync(update, progress, _updateCts.Token);
            // Let "Restarting" show for a beat, then hand over to the new copy (it waits for this one to exit).
            await Task.Delay(600);
            RestartForUpdateRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            IsUpdating = false;
        }
        catch (Exception ex)
        {
            UpdateError = ex is System.Net.Http.HttpRequestException
                ? "Couldn't reach GitHub. Check your connection and try again."
                : ex.Message;
            IsUpdating = false;
            IsUpdateScreenOpen = true; // say what went wrong, with Try again
        }
        finally
        {
            SetThreadExecutionState(EsContinuous);
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromHours(1)) return "just now";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} days ago";
        return $"on {when.ToLocalTime():MMM d, yyyy}";
    }
}
