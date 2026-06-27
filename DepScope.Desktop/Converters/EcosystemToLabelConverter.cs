using System;
using System.Globalization;
using Avalonia.Data.Converters;
using DepScope.Core.Models;

namespace DepScope.Desktop.Converters;

public sealed class EcosystemToLabelConverter : IValueConverter
{
    public static readonly EcosystemToLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Ecosystem eco)
            return string.Empty;

        return eco switch
        {
            Ecosystem.DotNet => ".NET",
            Ecosystem.Npm => "npm",
            Ecosystem.Python => "Python",
            Ecosystem.Java => "Java",
            Ecosystem.Php => "PHP",
            Ecosystem.Go => "Go",
            Ecosystem.Rust => "Rust",
            Ecosystem.GitHubActions => "GitHub Actions",
            _ => eco.ToString()
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
