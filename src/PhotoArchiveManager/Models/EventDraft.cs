namespace PhotoArchiveManager.Models;

public sealed class EventDraft
{
    public string Name { get; init; } = "";
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public double Confidence { get; init; }
    public double? CenterLatitude { get; init; }
    public double? CenterLongitude { get; init; }
    public IReadOnlyList<long> FileIds { get; init; } = Array.Empty<long>();
}
