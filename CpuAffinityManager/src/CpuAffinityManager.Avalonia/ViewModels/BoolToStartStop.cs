using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CpuAffinityManager.Avalonia.ViewModels;

/// <summary>
/// Converts the API-running flag to the toggle button label:
/// running → "停止 API", stopped → "启动 API".
/// </summary>
public sealed class BoolToStartStop : IValueConverter
{
    public static readonly BoolToStartStop Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "停止 API" : "启动 API";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
