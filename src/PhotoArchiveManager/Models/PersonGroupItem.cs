namespace PhotoArchiveManager.Models;

public sealed class PersonGroupItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int FaceCount { get; init; }
    public int PhotoCount { get; init; }
    public string RepresentativeThumbnailPath { get; init; } = "";
    public long? RepresentativeFaceId { get; init; }

    public bool IsUngrouped => Id == 0;
    public bool IsNamed => !IsUngrouped && !string.IsNullOrWhiteSpace(Name);
    public string DisplayName => IsUngrouped ? "Без группы" : IsNamed ? Name : $"Человек #{Id}";
    public string CountDisplay => $"лиц: {FaceCount:N0} · фото: {PhotoCount:N0}";
    public string CoverDisplay => RepresentativeFaceId.HasValue ? "Обложка выбрана вручную" : "Обложка выбрана автоматически";
}
