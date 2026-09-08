using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoArchiveManager.Infrastructure;
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
        // v1.8.1: old thumbnails for mirrored EXIF orientations (2/4/5/7) were cached
        // without applying the mirror/transpose. Change the cache key only for those cases
        // so corrected previews are regenerated without invalidating every existing thumbnail.
        var orientationCacheTag = orientation is 2 or 4 or 5 or 7 ? "|exif8-v1|" + orientation : "";
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            sourcePath.ToUpperInvariant() + "|" + fileSize + "|" + lastWriteTicks + orientationCacheTag));
        var key = Convert.ToHexString(keyBytes).ToLowerInvariant();
        var directory = Path.Combine(_root, key[..2]);
        var destination = Path.Combine(directory, key + ".jpg");

        try
        {
            int width;
            int height;
            using (var headerStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
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
                using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    bitmap.DecodePixelWidth = 360;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                BitmapSource source = ExifOrientationHelper.Apply(bitmap, orientation);
                source.Freeze();

                var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(BitmapFrame.Create(source));
                var temp = destination + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        encoder.Save(output);

                    // Two UI/background requests can legitimately race for the same cache key.
                    // The first complete thumbnail wins; the other temp file is simply discarded.
                    if (!File.Exists(destination))
                    {
                        try { File.Move(temp, destination); }
                        catch (IOException) when (File.Exists(destination)) { }
                    }
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }

            return new ThumbnailResult(destination, width, height, "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Some formats (notably HEIC/WEBP) depend on Windows codecs for preview.
            return new ThumbnailResult("", 0, 0, "Превью: " + ex.Message);
        }
    }

}
