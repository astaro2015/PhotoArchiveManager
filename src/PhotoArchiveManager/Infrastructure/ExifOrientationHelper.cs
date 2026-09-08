using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoArchiveManager.Infrastructure;

/// <summary>
/// Applies all eight EXIF Orientation values without modifying the source file.
/// Keeping this in one place prevents preview/hash/AI paths from disagreeing about
/// what the visually upright image actually is.
/// </summary>
internal static class ExifOrientationHelper
{
    public static bool SwapsDimensions(int orientation) => orientation is 5 or 6 or 7 or 8;

    public static BitmapSource Apply(BitmapSource source, int orientation)
    {
        return orientation switch
        {
            2 => Transform(source, new ScaleTransform(-1, 1)),
            3 => Transform(source, new RotateTransform(180)),
            4 => Transform(source, new ScaleTransform(1, -1)),
            5 => Transform(Transform(source, new ScaleTransform(-1, 1)), new RotateTransform(270)),
            6 => Transform(source, new RotateTransform(90)),
            7 => Transform(Transform(source, new ScaleTransform(-1, 1)), new RotateTransform(90)),
            8 => Transform(source, new RotateTransform(270)),
            _ => source
        };
    }

    private static BitmapSource Transform(BitmapSource source, Transform transform)
    {
        var transformed = new TransformedBitmap(source, transform);
        transformed.Freeze();
        return transformed;
    }
}
