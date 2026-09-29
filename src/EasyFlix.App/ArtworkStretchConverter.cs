using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EasyFlix.App;

public sealed class ArtworkStretchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        return string.IsNullOrWhiteSpace(path)
            ? Stretch.Uniform
            : Stretch.UniformToFill;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    public static bool IsLegacyFallbackPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && (path.EndsWith("-legacy.png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("-legacy.ico", StringComparison.OrdinalIgnoreCase));
}

public sealed class LegacyArtworkVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ArtworkStretchConverter.IsLegacyFallbackPath(value as string)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
