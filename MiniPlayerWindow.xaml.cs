using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Yesify.Models;
using Yesify.Services;
using Yesify.ViewModels;

namespace Yesify;

/// <summary>
/// Always-on-top, freely resizable player. The layout follows the window's shape: wide windows put the
/// cover on the left with the info beside it, tall/square ones put the cover on top. Controls and times
/// appear when there's room. The background is a gradient of the cover's colors.
/// </summary>
public partial class MiniPlayerWindow : Window, IFrostSource
{
    public IEnumerable<Visual> FrostLayers => [Root];

    private readonly MainViewModel _vm;
    private Track? _colorTrack;
    private IntPtr _hwnd;

    public MiniPlayerWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        var b = vm.MiniBounds;
        Width = Math.Clamp(b.Width, MinWidth, MaxWidth);
        Height = Math.Clamp(b.Height, MinHeight, MaxHeight);
        var area = SystemParameters.WorkArea;
        if (b.Left is { } l && b.Top is { } t &&
            l >= SystemParameters.VirtualScreenLeft && t >= SystemParameters.VirtualScreenTop &&
            l + Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            t + Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 24;
        }

        SizeChanged += (_, _) => ApplyLayout();
        // Clicking another app shouldn't push it behind anything: re-pin it above all windows.
        Deactivated += (_, _) => PinOnTop();
        _vm.PropertyChanged += OnVmPropertyChanged;
        WatchTrack(_vm.CurrentTrack);
    }

    // ---------------------------------------------------------------- layout

    private void ApplyLayout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var wide = w > h * 1.25;
        var pad = Math.Min(w, h) < 200 ? 10.0 : 16.0;
        Body.Margin = new Thickness(pad);

        bool showControls, showTimes;
        if (wide)
        {
            // [cover] [title / artist / progress / controls]
            CoverColumn.Width = new GridLength(Math.Max(0, h - 2 * pad));
            InfoColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(Info, 0);
            Grid.SetColumn(Info, 1);
            Grid.SetRowSpan(CoverHost, 2);
            Info.Margin = new Thickness(pad, 0, 0, 0);
            Info.VerticalAlignment = VerticalAlignment.Center;
            TitleText.HorizontalAlignment = ArtistText.HorizontalAlignment = HorizontalAlignment.Left;
            TitleText.Margin = new Thickness(0);
            TitleText.FontSize = Math.Clamp(h / 8, 13, 22);
            ArtistText.FontSize = Math.Clamp(h / 11, 11, 16);
            showControls = h >= 150 && w >= 360;
            showTimes = h >= 120;
        }
        else
        {
            // cover on top, info underneath
            CoverColumn.Width = new GridLength(1, GridUnitType.Star);
            InfoColumn.Width = new GridLength(0);
            Grid.SetRow(Info, 1);
            Grid.SetColumn(Info, 0);
            Grid.SetRowSpan(CoverHost, 1);
            Info.Margin = new Thickness(0);
            Info.VerticalAlignment = VerticalAlignment.Bottom;
            TitleText.HorizontalAlignment = ArtistText.HorizontalAlignment = HorizontalAlignment.Center;
            TitleText.Margin = new Thickness(0, w < 260 ? 8 : 14, 0, 0);
            TitleText.FontSize = Math.Clamp(w / 17, 12, 24);
            ArtistText.FontSize = Math.Clamp(w / 24, 11, 16);
            showControls = w >= 250 && h >= 300;
            showTimes = h >= 260;
        }

        Controls.Visibility = showControls ? Visibility.Visible : Visibility.Collapsed;
        Times.Visibility = showTimes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CoverHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var side = Math.Max(0, Math.Min(CoverHost.ActualWidth, CoverHost.ActualHeight));
        Cover.Width = Cover.Height = side;
        // Too little room for a useful cover (very short tall window): show just the info.
        Cover.Visibility = side < 36 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- background colors

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentTrack)) WatchTrack(_vm.CurrentTrack);
    }

    // Covers can arrive after the track starts (playlist covers load lazily), so follow ImageUrl too.
    private void WatchTrack(Track? track)
    {
        if (_colorTrack != null) _colorTrack.PropertyChanged -= OnTrackPropertyChanged;
        _colorTrack = track;
        if (track != null) track.PropertyChanged += OnTrackPropertyChanged;
        _ = UpdateColorsAsync(track);
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Track.ImageUrl)) _ = UpdateColorsAsync(_colorTrack);
    }

    private async Task UpdateColorsAsync(Track? track)
    {
        var (a, b) = await CoverColors.GetAsync(track?.ImageUrl);
        if (track != _colorTrack) return; // a newer song won the race
        var duration = TimeSpan.FromMilliseconds(700);
        StopA.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(a, duration));
        StopB.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(b, duration));
    }

    // ---------------------------------------------------------------- input

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cover_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _vm.TogglePlayCommand.Execute(null);
            e.Handled = true;
        }
        // A single press falls through to Root and drags the window.
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _vm.TogglePlayCommand.Execute(null);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void SeekBar_MouseDown(object sender, MouseButtonEventArgs e) => _vm.IsSeeking = true;

    private void SeekBar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _vm.SeekTo(SeekBar.Value);
            _vm.IsSeeking = false;
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.SaveMiniBounds(Left, Top, ActualWidth, ActualHeight);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        if (_colorTrack != null) _colorTrack.PropertyChanged -= OnTrackPropertyChanged;
        DataContext = null;
        base.OnClosed(e);
    }

    // ---------------------------------------------------------------- native bits

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Windows 11 rounded corners (and shadow) without a transparent window.
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        PinOnTop();
        ApplyLayout();
    }

    /// <summary>Moves the window to the top of the always-on-top band without stealing focus.</summary>
    private void PinOnTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        SetWindowPos(_hwnd, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
