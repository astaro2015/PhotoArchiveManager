namespace PhotoArchiveManager.Models;

public sealed class EventGroupItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public int PhotoCount { get; init; }
    public bool IsAuto { get; init; }
    public double Confidence { get; init; }
    public double? CenterLatitude { get; init; }
    public double? CenterLongitude { get; init; }
    public string PeopleNames { get; init; } = "";
    public string RepresentativeThumbnailPath { get; init; } = "";
    public long? RepresentativeFileId { get; init; }
    public string Notes { get; init; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DateRangeDisplay : Name;

    public string DateRangeDisplay
    {
        get
        {
            if (StartDate.Date == EndDate.Date)
                return $"{StartDate:dd.MM.yyyy} · {StartDate:HH:mm}–{EndDate:HH:mm}";
            return $"{StartDate:dd.MM.yyyy HH:mm} — {EndDate:dd.MM.yyyy HH:mm}";
        }
    }

    public string HeaderText => $"{DisplayName}  ·  {PhotoCount:N0} фото";
    public string TypeDisplay => IsAuto ? "Автоматическое" : "Закреплено пользователем";
    public string ConfidenceDisplay => IsAuto ? $"Уверенность: {Confidence * 100:0}%" : "Пользовательское событие";
    public string LocationDisplay => CenterLatitude.HasValue && CenterLongitude.HasValue
        ? $"GPS центр: {CenterLatitude.Value:0.00000}, {CenterLongitude.Value:0.00000}"
        : "GPS: —";
    public string PeopleDisplay => string.IsNullOrWhiteSpace(PeopleNames) ? "Люди: —" : "Люди: " + PeopleNames;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Notes) ? "Заметка: —" : Notes;
    public string CoverDisplay => RepresentativeFileId.HasValue ? "Обложка выбрана вручную" : "Обложка выбрана автоматически";
}
