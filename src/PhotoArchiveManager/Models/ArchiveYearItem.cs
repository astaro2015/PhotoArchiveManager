namespace PhotoArchiveManager.Models;

public sealed class ArchiveYearItem
{
    public int Year { get; init; }
    public IReadOnlyList<EventGroupItem> Events { get; init; } = Array.Empty<EventGroupItem>();

    public int EventCount => Events.Count;
    public int PhotoCount => Events.Sum(x => x.PhotoCount);
    public string DisplayName => Year > 0 ? Year.ToString() : "Без даты";
    public string CountDisplay => $"{EventCount:N0} событий · {PhotoCount:N0} фото";
}
