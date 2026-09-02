using System.Globalization;
using System.Windows.Data;

namespace PhotoArchiveManager.Converters;

public sealed class MultiplyConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        try
        {
            var a = System.Convert.ToDouble(values[0], CultureInfo.InvariantCulture);
            var b = System.Convert.ToDouble(values[1], CultureInfo.InvariantCulture);
            return Math.Max(0, a * b);
        }
        catch
        {
            return 0d;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
