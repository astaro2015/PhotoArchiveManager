namespace PhotoArchiveManager.Models;

public sealed class VisualDuplicateGroupItem
{
    public string GroupKey { get; init; } = "";
    public IReadOnlyList<VisualDuplicateFileItem> Files { get; init; } = Array.Empty<VisualDuplicateFileItem>();
    public int Threshold { get; init; }

    public int FileCount => Files.Count;
    public double MinimumSimilarity => Files.Count == 0 ? 0 : Files.Min(x => x.SimilarityPercent);
    public long TotalBytes => Files.Sum(x => x.FileSize);
    public string TotalBytesDisplay => ByteFormatter.Format(TotalBytes);
    public VisualDuplicateFileItem? RecommendedFile => Files.FirstOrDefault(x => x.IsRecommended);
    public int RatedCount => Files.Count(x => x.HasQuality);
    public string HeaderText => $"{FileCount:N0} визуально похожих · ≥ {MinimumSimilarity:0.0}%";
    public string SubheaderText => RatedCount > 0
        ? $"Общий объём: {TotalBytesDisplay} · оценено {RatedCount}/{FileCount} · лучший {RecommendedFile?.QualityDisplay ?? "—"}"
        : $"Общий объём: {TotalBytesDisplay} · dHash-порог: {Threshold} · качество ещё не оценено";
}
