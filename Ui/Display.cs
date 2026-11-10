using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Core.Models;

namespace Ui;

/// <summary>Formatting shared by the view models.</summary>
public static class Display
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }

    public static string? Hint(CompressibilityHint hint) => hint switch
    {
        CompressibilityHint.Good => "good",
        CompressibilityHint.Average => "average",
        CompressibilityHint.Poor => "poor",
        _ => null
    };

    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s"
        : $"{span.TotalSeconds:0.0}s";
}

/// <summary>Compressibility hint → badge color.</summary>
public sealed class HintBrushConverter : IValueConverter
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3D9A50"));
    private static readonly IBrush Average = new SolidColorBrush(Color.Parse("#C28A00"));
    private static readonly IBrush Poor = new SolidColorBrush(Color.Parse("#C2453A"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CompressibilityHint.Good => Good,
        CompressibilityHint.Average => Average,
        CompressibilityHint.Poor => Poor,
        _ => Brushes.Transparent
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Item state → badge color.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    private static readonly IBrush Done = new SolidColorBrush(Color.Parse("#3D9A50"));
    private static readonly IBrush Running = new SolidColorBrush(Color.Parse("#2F6FCF"));
    private static readonly IBrush Attention = new SolidColorBrush(Color.Parse("#C28A00"));
    private static readonly IBrush Failed = new SolidColorBrush(Color.Parse("#C2453A"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#7A7A7A"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        JobItemState.Done => Done,
        JobItemState.InProgress or JobItemState.Verified => Running,
        JobItemState.NeedsAttention => Attention,
        JobItemState.Failed => Failed,
        _ => Neutral
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
