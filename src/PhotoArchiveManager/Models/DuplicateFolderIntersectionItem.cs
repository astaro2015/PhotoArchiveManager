namespace PhotoArchiveManager.Models;

public sealed class DuplicateFolderIntersectionItem
{
    public int DisplayNumber { get; init; }
    public string FolderAPath { get; init; } = "";
    public string FolderBPath { get; init; } = "";
    public int SharedSetCount { get; init; }
    public int ACopyCount { get; init; }
    public long ABytes { get; init; }
    public int BCopyCount { get; init; }
    public long BBytes { get; init; }
    public int ATotalDuplicateSetCount { get; init; }
    public int BTotalDuplicateSetCount { get; init; }

    public string PairKey => BuildPairKey(FolderAPath, FolderBPath);
    public long MaxRemovableBytes => Math.Max(ABytes, BBytes);
    public bool ACoversAllDuplicateSets => SharedSetCount > 0 && SharedSetCount == ATotalDuplicateSetCount;
    public bool BCoversAllDuplicateSets => SharedSetCount > 0 && SharedSetCount == BTotalDuplicateSetCount;
    public bool CanFullyClearEitherSide => ACoversAllDuplicateSets || BCoversAllDuplicateSets;

    public string HeaderText => $"№{DisplayNumber:N0} · общих SHA-256: {SharedSetCount:N0}";
    public string SummaryText =>
        $"A: {ACopyCount:N0} ({ByteFormatter.Format(ABytes)}) · " +
        $"B: {BCopyCount:N0} ({ByteFormatter.Format(BBytes)}) · " +
        $"можно освободить до {ByteFormatter.Format(MaxRemovableBytes)}";

    public string CoverageText
    {
        get
        {
            if (ACoversAllDuplicateSets && BCoversAllDuplicateSets)
                return "✓ Эта пара покрывает ВСЕ найденные точные дубли обеих папок.";
            if (ACoversAllDuplicateSets)
                return "✓ Все найденные точные дубли папки A имеют копию в B.";
            if (BCoversAllDuplicateSets)
                return "✓ Все найденные точные дубли папки B имеют копию в A.";
            return "У обеих папок есть точные дубли и в других пересечениях.";
        }
    }

    public string ACoverageText => ACoversAllDuplicateSets
        ? "✓ Все найденные точные дубли A входят в эту пару."
        : $"Эта пара покрывает {SharedSetCount:N0} из {ATotalDuplicateSetCount:N0} наборов дублей A.";

    public string BCoverageText => BCoversAllDuplicateSets
        ? "✓ Все найденные точные дубли B входят в эту пару."
        : $"Эта пара покрывает {SharedSetCount:N0} из {BTotalDuplicateSetCount:N0} наборов дублей B.";

    public static string BuildPairKey(string first, string second)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(first, second) <= 0
            ? first + "\n" + second
            : second + "\n" + first;
    }
}
