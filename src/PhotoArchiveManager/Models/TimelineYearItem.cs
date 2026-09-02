namespace PhotoArchiveManager.Models;

public sealed class TimelineYearItem
{
    public int Year { get; init; }
    public int PhotoCount { get; init; }
    public IReadOnlyList<TimelineMonthItem> Months { get; init; } = Array.Empty<TimelineMonthItem>();
    public string DisplayName => $"{Year} · {PhotoCount:N0} фото";
}
