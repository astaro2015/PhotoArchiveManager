namespace PhotoArchiveManager.Models;

public sealed class ViewerPhotoItem
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public string CaptureDateDisplay { get; init; } = "";
    public string Details { get; init; } = "";

    public static ViewerPhotoItem FromPhoto(PhotoItem p) => new()
    {
        Id = p.Id,
        FullPath = p.FullPath,
        FileName = p.FileName,
        ThumbnailPath = p.ThumbnailPath,
        CaptureDateDisplay = p.CaptureDateDisplay,
        Details = $"{p.DimensionsDisplay} · {p.FileSizeDisplay} · {p.CameraDisplay}"
    };
}
