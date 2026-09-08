using PhotoArchiveManager.Infrastructure;

namespace PhotoArchiveManager.Models;

public sealed class BurstGroupItem : ObservableObject
{
    public string GroupKey { get; init; } = "";
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public IReadOnlyList<VisualDuplicateFileItem> Files { get; init; } = Array.Empty<VisualDuplicateFileItem>();

    private int _recommendedKeepCount = 1;
    public int RecommendedKeepCount
    {
        get => _recommendedKeepCount;
        private set => SetProperty(ref _recommendedKeepCount, value);
    }

    public int FileCount => Files.Count;
    public double DurationSeconds => Math.Max(0, (EndDate - StartDate).TotalSeconds);
    public int RatedCount => Files.Count(x => x.HasQuality);
    public int RecommendedCount => Files.Count(x => x.IsRecommended);
    public long TotalBytes => Files.Sum(x => x.FileSize);
    public string TotalBytesDisplay => ByteFormatter.Format(TotalBytes);
    public string DateDisplay => StartDate.Date == EndDate.Date
        ? StartDate.ToString("dd.MM.yyyy")
        : $"{StartDate:dd.MM.yyyy}–{EndDate:dd.MM.yyyy}";
    public string TimeRangeDisplay => StartDate.Date == EndDate.Date
        ? $"{StartDate:HH:mm:ss}–{EndDate:HH:mm:ss}"
        : $"{StartDate:dd.MM HH:mm:ss}–{EndDate:dd.MM HH:mm:ss}";

    public string HeaderText => $"{FileCount:N0} кадров · {DateDisplay} · {DurationSeconds:0.#} с";
    public string SubheaderText => RatedCount == FileCount && FileCount > 0
        ? $"{TimeRangeDisplay} · оценено {RatedCount}/{FileCount} · рекомендовано оставить {RecommendedCount} · {TotalBytesDisplay}"
        : $"{TimeRangeDisplay} · оценено {RatedCount}/{FileCount} · {TotalBytesDisplay}";

    public bool CanRecommend => Files.Count > 0 && RatedCount == FileCount;

    public void ApplyRecommendations(int keepCount)
    {
        keepCount = Math.Clamp(keepCount, 1, Math.Min(3, Math.Max(1, Files.Count)));
        RecommendedKeepCount = keepCount;

        foreach (var file in Files)
            file.IsRecommended = false;

        if (!CanRecommend)
        {
            NotifySummaryChanged();
            return;
        }

        foreach (var file in Files
                     .OrderByDescending(x => x.QualityScore)
                     .ThenBy(x => x.ClosedEyeCount > 0 ? x.ClosedEyeCount : 0)
                     .ThenByDescending(x => x.FaceCount > 0 ? x.WorstFaceScore : -1)
                     .ThenByDescending(x => x.SharpnessScore)
                     .ThenByDescending(x => x.ResolutionScore)
                     .ThenByDescending(x => x.FileSize)
                     .Take(keepCount))
            file.IsRecommended = true;

        NotifySummaryChanged();
    }

    public void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(RatedCount));
        OnPropertyChanged(nameof(RecommendedCount));
        OnPropertyChanged(nameof(SubheaderText));
        OnPropertyChanged(nameof(CanRecommend));
    }
}
