namespace PhotoArchiveManager.Models;

public enum PhotoReviewFilter
{
    All,
    NeedsReview,
    UntrustedDate,
    WithoutEvent,
    IndexError
}

public enum PhotoSortOrder
{
    CaptureDateDescending,
    CaptureDateAscending,
    FileNameAscending,
    FullPathAscending
}

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
    public PhotoReviewFilter ReviewFilter { get; init; } = PhotoReviewFilter.All;
    public PhotoSortOrder SortOrder { get; init; } = PhotoSortOrder.CaptureDateDescending;
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}
