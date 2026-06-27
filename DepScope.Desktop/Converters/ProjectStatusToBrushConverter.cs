using System;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DepScope.Core.Models;

namespace DepScope.Desktop.Converters;

public sealed class ProjectStatusToBrushConverter : IValueConverter
{
    // You can tweak these for dark mode
    public IBrush OkBrush { get; set; } = new SolidColorBrush(Color.Parse("#22C55E"));   // green
    public IBrush OutdatedBrush { get; set; } = new SolidColorBrush(Color.Parse("#F97316")); // orange
    public IBrush VulnerableBrush { get; set; } = new SolidColorBrush(Color.Parse("#EF4444")); // red
    public IBrush UnknownBrush { get; set; } = new SolidColorBrush(Color.Parse("#9CA3AF"));  // gray

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProjectInfo proj)
            return UnknownBrush;

        var hasPackages = proj.Packages.Any();
        if (!hasPackages)
            return UnknownBrush;

        var hasVulnerabilities = proj.Packages.Any(p =>
            p.VulnerabilityCount > 0 &&
            p.VulnerabilitySeverity != VulnerabilitySeverity.None &&
            p.VulnerabilitySeverity != VulnerabilitySeverity.NotApplicable &&
            p.VulnerabilitySeverity != VulnerabilitySeverity.NotChecked);
        if (hasVulnerabilities)
            return VulnerableBrush;

        // Any package with an update?
        var hasOutdated = proj.Packages.Any(p =>
            p.UpdateType == VersionUpdateType.Patch ||
            p.UpdateType == VersionUpdateType.Minor ||
            p.UpdateType == VersionUpdateType.Major);

        return hasOutdated ? OutdatedBrush : OkBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

