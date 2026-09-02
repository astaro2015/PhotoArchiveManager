namespace PhotoArchiveManager.Services;

public static class AppPaths
{
    // PAM is intentionally portable. The published application is a single EXE;
    // user data is created next to it on first run in the Data directory.
    private static readonly string BaseDirectory = AppContext.BaseDirectory;

    public static string DataDirectory => Path.Combine(BaseDirectory, "Data");
    public static string DatabasePath => Path.Combine(DataDirectory, "archive.db");
    public static string ThumbnailDirectory => Path.Combine(DataDirectory, "Cache", "Thumbnails");
    public static string FaceThumbnailDirectory => Path.Combine(DataDirectory, "Cache", "Faces");
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public static string QuarantineDirectory => Path.Combine(DataDirectory, "Quarantine");
    public static string ModelDirectory => Path.Combine(DataDirectory, "Models");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ThumbnailDirectory);
        Directory.CreateDirectory(FaceThumbnailDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(QuarantineDirectory);
        Directory.CreateDirectory(ModelDirectory);
    }
}
