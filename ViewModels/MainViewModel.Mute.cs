using CommunityToolkit.Mvvm.Input;

namespace Yesify.ViewModels;

/// <summary>Mute (the taskbar thumbnail's mute button): volume to zero, and back to where it was.</summary>
public sealed partial class MainViewModel
{
    private double _volumeBeforeMute = 0.8;

    public bool IsMuted => Volume <= 0.001;

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted) Volume = _volumeBeforeMute > 0.01 ? _volumeBeforeMute : 0.8;
        else
        {
            _volumeBeforeMute = Volume;
            Volume = 0;
        }
    }
}
