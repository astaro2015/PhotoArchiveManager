using PhotoArchiveManager.Infrastructure;

namespace PhotoArchiveManager.Models;

public sealed class OrganizationPlanItem : ObservableObject
{
    public long FileId { get; init; }
    public string SourcePath { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string DestinationRoot { get; init; } = "";
    public string FileName { get; init; } = "";
    public string TargetFileName { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public long FileSize { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public string CaptureDateSource { get; init; } = "";
    // Embedded EXIF capture date (timezone-less local camera time). When present, Organization
    // uses it for the destination file CreationTime; LastWriteTime is always preserved from source.
    public DateTime? EmbeddedCaptureDate { get; init; }
    public string DateDisplay { get; init; } = "";
    public string EventDisplay { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusDetails { get; init; } = "";
    public bool HasTrustedDate { get; init; }
    public bool IsReady { get; init; }
    public bool IsAlreadyCorrect { get; init; }
    public bool WasAutoRenamed { get; init; }
    public string CachedSha256 { get; init; } = "";
    public bool HasValidCachedSha256 { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, IsReady && value);
    }

    public string FileSizeDisplay => ByteFormatter.Format(FileSize);
    public string MoveDisplay => SourcePath + "\n→ " + TargetPath;
}
