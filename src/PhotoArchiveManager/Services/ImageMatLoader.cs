using OpenCvSharp;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoArchiveManager.Services;

internal static class ImageMatLoader
{
    public static Mat LoadBgr(string path, int orientation, int maxDimension = 2400)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidDataException("Изображение не содержит декодируемого кадра.");

        BitmapSource source = decoder.Frames[0];
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            throw new InvalidDataException("Некорректный размер изображения.");

        if (orientation is 3 or 6 or 8)
        {
            var angle = orientation == 3 ? 180 : orientation == 6 ? 90 : 270;
            var rotated = new TransformedBitmap(source, new RotateTransform(angle));
            rotated.Freeze();
            source = rotated;
        }

        var largest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxDimension > 0 && largest > maxDimension)
        {
            var scale = maxDimension / (double)largest;
            var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            resized.Freeze();
            source = resized;
        }

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
