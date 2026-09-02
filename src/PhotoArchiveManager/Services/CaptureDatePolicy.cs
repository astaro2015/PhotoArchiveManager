namespace PhotoArchiveManager.Services;

public static class CaptureDatePolicy
{
    public const string ManualCatalog = "Ручная дата (каталог)";

    public static bool IsManual(string? source) =>
        string.Equals(source, ManualCatalog, StringComparison.Ordinal);

    public static bool IsTrusted(string? source) =>
        !string.IsNullOrWhiteSpace(source) &&
        (source.StartsWith("EXIF", StringComparison.OrdinalIgnoreCase) || IsManual(source));
}
