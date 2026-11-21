using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;

namespace Yesify.ViewModels;

/// <summary>
/// Settings → Background: the album cover (blurred, tinted with its colors) or a custom two-color gradient;
/// how strong the tint is, how bright, and whether it slowly moves. MainWindow applies these to the backdrop
/// whenever one of the Backdrop* properties changes.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>"album" or "color".</summary>
    public string BackdropMode
    {
        get => _settings.BackdropMode;
        set => SetBackdrop(value is "color" ? "color" : "album", _settings.BackdropMode, v => _settings.BackdropMode = v);
    }

    public double BackdropTint
    {
        get => _settings.BackdropTint;
        set => SetBackdrop(Math.Round(Math.Clamp(value, 0, 100)), _settings.BackdropTint, v => _settings.BackdropTint = v);
    }

    public double BackdropBrightness
    {
        get => _settings.BackdropBrightness;
        set => SetBackdrop(Math.Round(Math.Clamp(value, 0, 100)), _settings.BackdropBrightness, v => _settings.BackdropBrightness = v);
    }

    public bool BackdropMotion
    {
        get => _settings.BackdropMotion;
        set => SetBackdrop(value, _settings.BackdropMotion, v => _settings.BackdropMotion = v);
    }

    /// <summary>Hex text (#RRGGBB). Anything that isn't a color yet (half typed) is simply not applied.</summary>
    public string BackdropColor1
    {
        get => _settings.BackdropColor1;
        set { if (ParseColor(value) is { } c) SetBackdrop(Hex(c), _settings.BackdropColor1, v => _settings.BackdropColor1 = v, nameof(BackdropBrush1)); }
    }

    public string BackdropColor2
    {
        get => _settings.BackdropColor2;
        set { if (ParseColor(value) is { } c) SetBackdrop(Hex(c), _settings.BackdropColor2, v => _settings.BackdropColor2 = v, nameof(BackdropBrush2)); }
    }

    public Color BackdropColorA => ParseColor(_settings.BackdropColor1) ?? Color.FromRgb(0x3A, 0x2A, 0x8C);
    public Color BackdropColorB => ParseColor(_settings.BackdropColor2) ?? Color.FromRgb(0x0E, 0x0B, 0x22);
    public Brush BackdropBrush1 => Frozen(new SolidColorBrush(BackdropColorA));
    public Brush BackdropBrush2 => Frozen(new SolidColorBrush(BackdropColorB));

    /// <summary>Ready-made pairs for the custom background ("#top,#bottom").</summary>
    public IReadOnlyList<string> BackdropPresets { get; } =
    [
        "#3A2A8C,#0E0B22", // violet night
        "#1F4E9C,#07142B", // deep blue
        "#0F6B5C,#04201B", // teal
        "#8C2A4A,#220A14", // wine
        "#9C5A1F,#2B1607", // amber
        "#2E7D32,#0A2410", // forest
        "#5A5A66,#141418", // graphite
        "#101012,#000000", // black
    ];

    [RelayCommand]
    private void SetBackdropPreset(string? pair)
    {
        if (pair?.Split(',') is not [var a, var b]) return;
        BackdropColor1 = a;
        BackdropColor2 = b;
        BackdropMode = "color";
        OnPropertyChanged(nameof(BackdropColor1));
        OnPropertyChanged(nameof(BackdropColor2));
    }

    [RelayCommand]
    private void SetBackdropMode(string? mode) => BackdropMode = mode ?? "album";

    [RelayCommand]
    private void ResetBackdrop()
    {
        BackdropMode = "album";
        BackdropTint = 50;
        BackdropBrightness = 50;
        BackdropMotion = false;
        BackdropMotionFps = 30;
    }

    private void SetBackdrop<T>(T value, T current, Action<T> store, string? also = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current)) return;
        store(value);
        SaveSettings();
        OnPropertyChanged(nameof(BackdropMode));
        OnPropertyChanged(nameof(BackdropTint));
        OnPropertyChanged(nameof(BackdropBrightness));
        OnPropertyChanged(nameof(BackdropMotion));
        OnPropertyChanged(nameof(BackdropMotionFps));
        if (also != null) OnPropertyChanged(also);
        BackdropSettingsChanged?.Invoke();
    }

    /// <summary>Raised when any background setting changes (MainWindow re-applies the backdrop).</summary>
    public event Action? BackdropSettingsChanged;

    private static Color? ParseColor(string? text)
    {
        text = text?.Trim() ?? "";
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7) return null;
        try { return (Color)ColorConverter.ConvertFromString(text); }
        catch { return null; }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static T Frozen<T>(T f) where T : System.Windows.Freezable
    {
        f.Freeze();
        return f;
    }
}
