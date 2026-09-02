namespace PhotoArchiveManager.Models;

public sealed class TimelineMonthItem
{
    public int Year { get; init; }
    public int Month { get; init; }
    public int PhotoCount { get; init; }
    public string RepresentativeThumbnailPath { get; init; } = "";

    public string MonthName => new DateTime(Year, Month, 1).ToString("MMMM", new System.Globalization.CultureInfo("ru-RU"));
    public string DisplayName => $"{char.ToUpper(MonthName[0], new System.Globalization.CultureInfo("ru-RU")) + MonthName[1..]} · {PhotoCount:N0}";
    public DateTime StartDate => new(Year, Month, 1);
    public DateTime EndDateExclusive => StartDate.AddMonths(1);
}
