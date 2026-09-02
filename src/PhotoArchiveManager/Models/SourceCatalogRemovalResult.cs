namespace PhotoArchiveManager.Models;

public sealed class SourceCatalogRemovalResult
{
    public int DeletedCatalogRecords { get; init; }
    public int AuditRecordsRetained { get; init; }
    public int ActiveQuarantineCount { get; init; }
    public List<string> CacheFilesToDelete { get; init; } = new();
}
