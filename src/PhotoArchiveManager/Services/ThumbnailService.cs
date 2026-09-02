using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class ThumbnailService
{
    private readonly string _root;

    public ThumbnailService(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public Task<ThumbnailResult> CreateAsync(string sourcePath, long fileSize, long lastWriteTicks, int orientation, CancellationToken cancellationToken)
        => Task.Run(() => Create(sourcePath, fileSize, lastWriteTicks, orientation, cancellationToken), cancellationToken);

    private ThumbnailResult Create(string sourcePath, long fileSize, long lastWriteTicks, int orientation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant() + "|" + fileSize + "|" + lastWriteTicks));
        var key = Convert.ToHexString(keyBytes).ToLowerInvariant();
        var directory = Path.Combine(_root, key[..2]);
        var destination = Path.Combine(directory, key + ".jpg");

        try
        {
            int width;
            int height;
            using (var headerStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(headerStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }

            if (!File.Exists(destination))
            {
                Directory.CreateDirectory(directory);
                cancellationToken.ThrowIfCancellationRequested();

                BitmapImage bitmap = new();
                using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    bitmap.DecodePixelWidth = 360;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                BitmapSource source = ApplyOrientation(bitmap, orientation);
                source.Freeze();

                var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(BitmapFrame.Create(source));
                var temp = destination + ".tmp";
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    encoder.Save(output);
                File.Move(temp, destination, true);
            }

            return new ThumbnailResult(destination, width, height, "");
        }
        catch (Exception ex)
        {
            // Some formats (notably HEIC/WEBP) depend on Windows codecs for preview.
            return new ThumbnailResult("", 0, 0, "Превью: " + ex.Message);
        }
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        double angle = orientation switch
        {
            3 => 180,
            6 => 90,
            8 => 270,
            _ => 0
        };

        if (angle == 0) return source;
        var transformed = new TransformedBitmap(source, new RotateTransform(angle));
        transformed.Freeze();
        return transformed;
    }
}
