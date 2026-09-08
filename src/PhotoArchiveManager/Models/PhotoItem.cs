using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Services;

namespace PhotoArchiveManager.Models;

public sealed class PhotoItem
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public long FileSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? CaptureDate { get; init; }
    public string CaptureDateSource { get; init; } = "";
    public string CameraMake { get; init; } = "";
    public string CameraModel { get; init; } = "";
    public string Error { get; init; } = "";

    public bool IsManualCaptureDate => CaptureDatePolicy.IsManual(CaptureDateSource);

    public string CameraDisplay
    {
        get
        {
            var value = string.Join(" ", new[] { CameraMake, CameraModel }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            return string.IsNullOrWhiteSpace(value) ? "Камера: —" : value;
        }
    }

    public string CaptureDateDisplay =>
        StoredDateTime.TryParse(CaptureDate, out var value) ? value.ToString("dd.MM.yyyy HH:mm:ss") : "Дата: неизвестна";

    public string DimensionsDisplay => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "Размер: —";

    public string FileSizeDisplay
    {
        get
        {
            double size = FileSize;
            string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.##} {units[unit]}";
        }
    }
}
