using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DepScope.Core.Models;

namespace DepScope.Desktop.Converters;

public sealed class UpdateTypeToBrushConverter : IValueConverter
{
    public static readonly UpdateTypeToBrushConverter Instance = new();

    // Pre-create brushes so we don't recreate them every call
    private static readonly IBrush NoneBrush = new SolidColorBrush(Color.Parse("#22C55E")); // now green old choice #4B5563 gray-600
    private static readonly IBrush PatchBrush = new SolidColorBrush(Color.Parse("#F59E0B")); // now amber-500 old choice #10B981 emerald-500
    private static readonly IBrush MinorBrush = new SolidColorBrush(Color.Parse("#F97316")); // orange
    private static readonly IBrush MajorBrush = new SolidColorBrush(Color.Parse("#EF4444")); // red-500
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#6B7280")); // gray-500

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not VersionUpdateType updateType)
            return UnknownBrush;

        return updateType switch
        {
            VersionUpdateType.None => NoneBrush,
            VersionUpdateType.Patch => PatchBrush,
            VersionUpdateType.Minor => MinorBrush,
            VersionUpdateType.Major => MajorBrush,
            _ => UnknownBrush
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

