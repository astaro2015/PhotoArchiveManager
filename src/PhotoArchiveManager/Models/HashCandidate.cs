namespace PhotoArchiveManager.Models;

public sealed class HashCandidate
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public long FileSize { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public string Sha256 { get; init; } = "";
    public long HashFileSize { get; init; }
    public long HashLastWriteUtcTicks { get; init; }
}
