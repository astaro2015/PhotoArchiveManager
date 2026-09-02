namespace PhotoArchiveManager.Models;

public sealed class MetadataResult
{
    public DateTime? CaptureDate { get; init; }
    public string CaptureDateSource { get; init; } = "";
    public string CameraMake { get; init; } = "";
    public string CameraModel { get; init; } = "";
    public int Orientation { get; init; } = 1;
    public double? GpsLatitude { get; init; }
    public double? GpsLongitude { get; init; }
    public string Error { get; init; } = "";
}
