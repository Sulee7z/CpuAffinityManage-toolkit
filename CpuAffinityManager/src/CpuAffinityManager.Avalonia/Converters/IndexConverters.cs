using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CpuAffinityManager.Avalonia.Converters;

/// <summary>int == 0 → true(用于按钮高亮)。</summary>
public sealed class IndexIsZeroConverter : IValueConverter
{
    public static readonly IndexIsZeroConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int == 1 → true。</summary>
public sealed class IndexIsOneConverter : IValueConverter
{
    public static readonly IndexIsOneConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 1;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int == 2 → true。</summary>
public sealed class IndexIsTwoConverter : IValueConverter
{
    public static readonly IndexIsTwoConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 2;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int == 3 → true。</summary>
public sealed class IndexIsThreeConverter : IValueConverter
{
    public static readonly IndexIsThreeConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 3;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int &gt;= 0 → true(有匹配的生效项)。</summary>
public sealed class IndexIsNonNegativeConverter : IValueConverter
{
    public static readonly IndexIsNonNegativeConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i >= 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
