using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CpuAffinityManager.App.Converters;

/// <summary>
/// Maps enforcement level to a color for visual distinction in the UI.
/// </summary>
public class RuleLevelToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string level = value as string ?? "";

        return level switch
        {
            "soft-cpu-sets" => new SolidColorBrush(Color.FromRgb(144, 202, 249)),  // Light blue
            "hard-affinity" => new SolidColorBrush(Color.FromRgb(255, 204, 128)),  // Orange
            "job-enforced" => new SolidColorBrush(Color.FromRgb(239, 154, 154)),   // Light red
            "job-locked" => new SolidColorBrush(Color.FromRgb(239, 83, 80)),       // Strong red
            _ => new SolidColorBrush(Colors.Gray)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
