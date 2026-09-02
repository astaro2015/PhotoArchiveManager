namespace PhotoArchiveManager.Models;

public sealed class EventCandidate
{
    public long FileId { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public DateTime CaptureDate { get; init; }
    public string CaptureDateSource { get; init; } = "";
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public IReadOnlySet<long> PersonIds { get; init; } = new HashSet<long>();
    public IReadOnlyList<string> PersonNames { get; init; } = Array.Empty<string>();
}
