namespace PhotoArchiveManager.Models;

public sealed class PhotoRecord
{
    public long Id { get; set; }
    public string FullPath { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long FileSize { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public long CreationUtcTicks { get; set; }
    public string? CaptureDate { get; set; }
    public string CaptureDateSource { get; set; } = "";
    public int EffectiveYear { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string CameraMake { get; set; } = "";
    public string CameraModel { get; set; } = "";
    public int Orientation { get; set; } = 1;
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public string ThumbnailPath { get; set; } = "";
    public string Error { get; set; } = "";
    public string LastSeenScanId { get; set; } = "";
}
