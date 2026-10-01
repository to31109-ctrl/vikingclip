using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VikingClip.Core.Util;

namespace VikingClip.App;

public sealed class HumanSizeConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is long l ? FileNames.HumanSize(l) : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class HumanDurationConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d && d > 0 ? FileNames.HumanDuration(d) : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class TimeAgoConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not DateTime dt) return "";
        var span = DateTime.Now - dt;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
        return dt.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>null/empty → Collapsed. With ConverterParameter "invert": null/empty → Visible (placeholders).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var empty = value is null || (value is string s && s.Length == 0);
        var invert = string.Equals(p?.ToString(), "invert", StringComparison.OrdinalIgnoreCase);
        return empty ^ invert ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Equals(value?.ToString(), p?.ToString());
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is true ? Enum.Parse(t, p!.ToString()!) : Binding.DoNothing;
}
