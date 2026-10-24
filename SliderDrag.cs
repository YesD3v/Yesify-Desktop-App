using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Yesify;

/// <summary>
/// Pointer handling for the flat sliders (their template has no Track/Thumb): press or drag anywhere
/// sets the value under the pointer, edge to edge, and the mouse wheel steps by <see cref="WheelStepProperty"/>.
/// Values are set with SetCurrentValue so one-way bindings (the seek bar) keep working.
/// </summary>
public static class SliderDrag
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SliderDrag), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Wheel step in value units; 0 disables the wheel (the seek bar).</summary>
    public static readonly DependencyProperty WheelStepProperty = DependencyProperty.RegisterAttached(
        "WheelStep", typeof(double), typeof(SliderDrag), new PropertyMetadata(0.0));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static double GetWheelStep(DependencyObject d) => (double)d.GetValue(WheelStepProperty);
    public static void SetWheelStep(DependencyObject d, double value) => d.SetValue(WheelStepProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider s) return;
        if ((bool)e.NewValue)
        {
            s.MouseLeftButtonDown += OnDown;
            s.MouseMove += OnMove;
            s.MouseLeftButtonUp += OnUp;
            s.MouseWheel += OnWheel;
        }
        else
        {
            s.MouseLeftButtonDown -= OnDown;
            s.MouseMove -= OnMove;
            s.MouseLeftButtonUp -= OnUp;
            s.MouseWheel -= OnWheel;
        }
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        var s = (Slider)sender;
        s.CaptureMouse();
        SetFromPointer(s, e);
        e.Handled = true; // don't start a window drag (miniplayer)
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        var s = (Slider)sender;
        if (s.IsMouseCaptured) SetFromPointer(s, e);
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var s = (Slider)sender;
        if (!s.IsMouseCaptured) return;
        SetFromPointer(s, e);
        s.ReleaseMouseCapture();
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var s = (Slider)sender;
        var step = GetWheelStep(s);
        if (step <= 0) return;
        var v = Math.Clamp(s.Value + Math.Sign(e.Delta) * step, s.Minimum, s.Maximum);
        s.SetCurrentValue(RangeBase.ValueProperty, Math.Round(v / step) * step);
        e.Handled = true;
    }

    private static void SetFromPointer(Slider s, MouseEventArgs e)
    {
        if (s.ActualWidth <= 0) return;
        var fraction = Math.Clamp(e.GetPosition(s).X / s.ActualWidth, 0, 1);
        s.SetCurrentValue(RangeBase.ValueProperty, s.Minimum + fraction * (s.Maximum - s.Minimum));
    }
}
