namespace PhotoArchiveManager.Models;

public sealed class EventMetadataCandidate
{
    public long FileId { get; init; }
    public string FullPath { get; init; } = "";
    public long FileSize { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public int GpsIndexVersion { get; init; }
    public long GpsIndexFileSize { get; init; }
    public long GpsIndexLastWriteUtcTicks { get; init; }
}
