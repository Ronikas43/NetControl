using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NetControl.Models;

namespace NetControl.Controls;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is Visibility vis && vis == Visibility.Visible;
}

/// <summary>Collapses an element when the bound string is null/empty/whitespace.</summary>
public class StringEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool empty = string.IsNullOrWhiteSpace(value as string);
        if (Invert) empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class OnlineToBrushConverter : IValueConverter
{
    private static readonly Brush Online = Frozen(Color.FromRgb(0x34, 0xC7, 0x7B));
    private static readonly Brush Offline = Frozen(Color.FromRgb(0x9C, 0x9C, 0xAF));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is bool v && v ? Online : Offline;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public string TrueColor { get; set; } = "#F0506E";
    public string FalseColor { get; set; } = "#9C9CAF";

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        var hex = b ? TrueColor : FalseColor;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var sev = (value as string) ?? "Info";
        var hex = sev switch
        {
            "Critical" => "#F0506E",
            "Warning" => "#F5A623",
            _ => "#4F8CFF"
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class DeviceCategoryConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is DeviceCategory cat ? cat.ToString() : value?.ToString() ?? "Other";

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        if (value is string s && Enum.TryParse<DeviceCategory>(s, out var cat)) return cat;
        return DeviceCategory.Other;
    }
}

public class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "Yes";
    public string FalseText { get; set; } = "No";
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is bool b && b ? TrueText : FalseText;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
