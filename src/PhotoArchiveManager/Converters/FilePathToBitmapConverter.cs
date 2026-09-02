using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace PhotoArchiveManager.Converters;

public sealed class FilePathToBitmapConverter : IValueConverter
{
    // WPF recycling asks the converter for the same thumbnails repeatedly while scrolling.
    // Keep a small bounded cache of frozen bitmaps: enough to make back/forward scrolling
    // smooth without letting a large photo library grow process memory without bound.
    private const int CacheCapacity = 160;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Bitmap)>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<(string Key, BitmapSource Bitmap)> Lru = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var decodeWidth = 0;
            if (parameter is not null)
                int.TryParse(parameter.ToString(), out decodeWidth);

            var cacheKey = path + "|" + decodeWidth.ToString(CultureInfo.InvariantCulture);
            if (TryGetCached(cacheKey, out var cached)) return cached;

            var bitmap = new BitmapImage();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            AddCached(cacheKey, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetCached(string key, out BitmapSource? bitmap)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out var node))
            {
                bitmap = null;
                return false;
            }
            Lru.Remove(node);
            Lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }
    }

    private static void AddCached(string key, BitmapSource bitmap)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                existing.Value = (key, bitmap);
                Lru.Remove(existing);
                Lru.AddFirst(existing);
                return;
            }

            var node = Lru.AddFirst((key, bitmap));
            Cache[key] = node;
            while (Cache.Count > CacheCapacity && Lru.Last is { } last)
            {
                Cache.Remove(last.Value.Key);
                Lru.RemoveLast();
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
