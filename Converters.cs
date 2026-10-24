using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yesify.Models;

namespace Yesify;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var hasValue = value is string s ? s.Length > 0 : value != null;
        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class PlayGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? "" : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class RepeatGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is RepeatMode.One ? "" : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Accent brush when "on" (true, or RepeatMode other than Off), muted otherwise.</summary>
public sealed class ActiveBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var on = value is true || value is RepeatMode m && m != RepeatMode.Off;
        return Application.Current.Resources[on ? "AccentBrush" : "InactiveIconBrush"];
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// Value, Minimum, Maximum, width → fill width (no parameter) or knob offset (parameter = knob size,
/// kept inside the rail).
/// </summary>
public sealed class SliderFillConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c)
    {
        if (values.Length < 4 || values[0] is not double v || values[1] is not double min || values[2] is not double max || values[3] is not double width)
            return 0.0;
        var fraction = max > min ? Math.Clamp((v - min) / (max - min), 0, 1) : 0;
        if (p is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var knob))
            return fraction * Math.Max(0, width - knob);
        return fraction * width;
    }

    public object[] ConvertBack(object? value, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is double d ? $"Volume {Math.Round(d * 100)}%" : "";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the bound count is 0 (empty-state hints).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Background of a tab pill: highlighted when its tab is active.</summary>
public sealed class TabBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Application.Current.Resources["SelectedBrush"] : Brushes.Transparent;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>The header row gets a plain container; song rows get the normal one.</summary>
public sealed class RowStyleSelector : StyleSelector
{
    public Style? Default { get; set; }
    public Style? Plain { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is HeaderMarker ? Plain : Default;
}

/// <summary>True when both bound values are the same object.</summary>
public sealed class SameObjectConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c) =>
        values.Length == 2 && values[0] != null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}


/// <summary>Sidebar labels fade in over the last part of the sidebar opening (and out as it closes).</summary>
public sealed class SidebarLabelOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is double w ? Math.Clamp((w - 130) / 100, 0, 1) : 1.0;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
