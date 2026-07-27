using System.Globalization;
using System.Windows.Data;

namespace CpuAffinityManager.App.Converters;

/// <summary>
/// Converts a ulong affinity mask to a boolean array for checkbox binding.
/// </summary>
public class MaskToBitArrayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ulong mask)
        {
            var bits = new bool[64];
            for (int i = 0; i < 64; i++)
                bits[i] = (mask & (1UL << i)) != 0;
            return bits;
        }
        return new bool[64];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool[] bits)
        {
            ulong mask = 0;
            for (int i = 0; i < Math.Min(bits.Length, 64); i++)
            {
                if (bits[i])
                    mask |= 1UL << i;
            }
            return mask;
        }
        return 0UL;
    }
}
