using OpenCvSharp;
using PhotoArchiveManager.Infrastructure;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoArchiveManager.Services;

internal static class ImageMatLoader
{
    /// <summary>
    /// Decodes close to the requested working size instead of materializing the full source bitmap
    /// first. This matters for 40–100 MP photos and is shared by Quality/People/hash paths.
    /// The returned bitmap is already normalized for all eight EXIF Orientation values.
    /// </summary>
    public static BitmapSource LoadBitmapSource(string path, int orientation, int maxDimension = 2400)
    {
        int rawWidth;
        int rawHeight;
        using (var headerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var headerDecoder = BitmapDecoder.Create(headerStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (headerDecoder.Frames.Count == 0)
                throw new InvalidDataException("Изображение не содержит декодируемого кадра.");
            rawWidth = headerDecoder.Frames[0].PixelWidth;
            rawHeight = headerDecoder.Frames[0].PixelHeight;
        }

        if (rawWidth <= 0 || rawHeight <= 0)
            throw new InvalidDataException("Некорректный размер изображения.");

        BitmapImage bitmap = new();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (maxDimension > 0 && Math.Max(rawWidth, rawHeight) > maxDimension)
            {
                if (rawWidth >= rawHeight) bitmap.DecodePixelWidth = maxDimension;
                else bitmap.DecodePixelHeight = maxDimension;
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
        }

        BitmapSource source = ExifOrientationHelper.Apply(bitmap, orientation);

        // A codec is allowed to ignore DecodePixelWidth/Height. Enforce the bound as a fallback.
        var largest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxDimension > 0 && largest > maxDimension)
        {
            var scale = maxDimension / (double)largest;
            var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            resized.Freeze();
            source = resized;
        }

        source.Freeze();
        return source;
    }

    public static Mat LoadBgr(string path, int orientation, int maxDimension = 2400)
    {
        var source = LoadBitmapSource(path, orientation, maxDimension);
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        using var matBgra = new Mat(bgra.PixelHeight, bgra.PixelWidth, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, matBgra.Data, pixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(matBgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }
}
