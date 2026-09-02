namespace PhotoArchiveManager.Models;

public sealed class PhotoQuery
{
    public string SearchText { get; init; } = "";
    public int? Year { get; init; }
    public string Camera { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public long? PersonId { get; init; }
    public long? EventId { get; init; }
    public DateTime? DateFrom { get; init; }
    public DateTime? DateToExclusive { get; init; }
    public bool FavoriteOnly { get; init; }
    public int MinRating { get; init; }
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}
