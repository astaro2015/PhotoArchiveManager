using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoArchiveManager.Converters;

public sealed class OrientedFileToBitmapConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var orientation = values.Length > 1 && values[1] is int value ? value : 1;
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            if (orientation is not (3 or 6 or 8)) return bitmap;
            var angle = orientation == 3 ? 180 : orientation == 6 ? 90 : 270;
            var rotated = new TransformedBitmap(bitmap, new RotateTransform(angle));
            rotated.Freeze();
            return rotated;
        }
        catch
        {
            return null;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
