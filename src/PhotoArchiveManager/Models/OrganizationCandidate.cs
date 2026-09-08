namespace PhotoArchiveManager.Models;

public sealed class OrganizationCandidate
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string FileName { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public long FileSize { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public string? CaptureDate { get; init; }
    public string CaptureDateSource { get; init; } = "";
    public string? AutoCaptureDate { get; init; }
    public string AutoCaptureDateSource { get; init; } = "";
    public string EventName { get; init; } = "";
    public bool EventIsAuto { get; init; }
    public string? EventStartDate { get; init; }
    public string? EventEndDate { get; init; }
    public string Sha256 { get; init; } = "";
    public long HashFileSize { get; init; }
    public long HashLastWriteUtcTicks { get; init; }
    public IReadOnlyList<string> NamedPeople { get; init; } = Array.Empty<string>();
}
