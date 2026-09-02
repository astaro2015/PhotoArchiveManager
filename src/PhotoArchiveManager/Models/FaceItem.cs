using PhotoArchiveManager.Infrastructure;

namespace PhotoArchiveManager.Models;

public sealed class FaceItem : ObservableObject
{
    public long FaceId { get; init; }
    public long FileId { get; init; }
    public long? PersonId { get; init; }
    public string PersonName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FaceThumbnailPath { get; init; } = "";
    public string PhotoThumbnailPath { get; init; } = "";
    public string? CaptureDate { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double QualityScore { get; init; }
    public bool IsIgnored { get; init; }

    public string CaptureDateDisplay => DateTime.TryParse(CaptureDate, out var value)
        ? value.ToString("dd.MM.yyyy HH:mm")
        : "Дата неизвестна";
    public string QualityDisplay => $"лицо {QualityScore:0}/100";
    public string GroupDisplay => PersonId.HasValue
        ? (string.IsNullOrWhiteSpace(PersonName) ? $"Человек #{PersonId.Value}" : PersonName)
        : "Без группы";
}
