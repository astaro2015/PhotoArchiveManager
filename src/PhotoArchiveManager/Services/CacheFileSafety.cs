namespace PhotoArchiveManager.Services;

/// <summary>
/// Hard boundary for cache cleanup. Paths read from SQLite are never trusted as deletion targets
/// unless they match one of PAM's exact generated-cache layouts. Original photographs and arbitrary
/// user paths therefore cannot be deleted by cache-maintenance code even if a catalogue row is stale,
/// damaged or manually edited.
/// </summary>
internal static class CacheFileSafety
{
    public static bool IsGeneratedCachePath(string? path) =>
        TryGetGeneratedCacheFullPath(path, out _);

    public static bool TryDeleteGeneratedCacheFile(string? path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (!TryGetGeneratedCacheFullPath(path, out var fullPath))
        {
            reason = "путь не соответствует штатному формату файла Data\\Cache";
            return false;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "cache-файл является reparse point/symlink";
                    return false;
                }
                File.Delete(fullPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool TryGetGeneratedCacheFullPath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            fullPath = Path.GetFullPath(path);
            return IsFaceThumbnailPath(fullPath) || IsLibraryThumbnailPath(fullPath);
        }
        catch
        {
            fullPath = "";
            return false;
        }
    }

    private static bool IsFaceThumbnailPath(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !IsSameDirectory(parent, AppPaths.FaceThumbnailDirectory))
            return false;
        if (IsReparsePointDirectory(parent)) return false;

        // PeopleAnalyzer creates exactly f<FileId>_<32 hex Guid>.jpg in Data\Cache\Faces.
        var fileName = Path.GetFileName(fullPath);
        if (!fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 35 || stem[0] != 'f') return false;
        var separator = stem.IndexOf('_', 1);
        if (separator <= 1 || separator != stem.Length - 33) return false;
        if (!stem.AsSpan(1, separator - 1).ToString().All(char.IsDigit)) return false;
        return IsHex(stem.AsSpan(separator + 1));
    }

    private static bool IsLibraryThumbnailPath(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent)) return false;
        var shardName = Path.GetFileName(parent);
        var root = Path.GetDirectoryName(parent);
        if (string.IsNullOrWhiteSpace(root) || !IsSameDirectory(root, AppPaths.ThumbnailDirectory))
            return false;
        if (IsReparsePointDirectory(root)) return false;

        // ThumbnailService shards by the first two SHA-256 hex characters and names the JPEG
        // with the complete 64-character hash. Do not accept arbitrary nested paths from SQLite.
        if (shardName.Length != 2 || !IsHex(shardName.AsSpan())) return false;
        if (IsReparsePointDirectory(parent)) return false;

        var fileName = Path.GetFileName(fullPath);
        if (!fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length != 64 || !IsHex(stem.AsSpan())) return false;
        return stem.StartsWith(shardName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return false;
        foreach (var ch in value)
        {
            if (!((ch >= '0' && ch <= '9') ||
                  (ch >= 'a' && ch <= 'f') ||
                  (ch >= 'A' && ch <= 'F')))
                return false;
        }
        return true;
    }

    private static bool IsReparsePointDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // If attributes cannot be verified, fail closed for deletion safety.
            return true;
        }
    }

    private static bool IsSameDirectory(string left, string right)
    {
        var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
