namespace PhotoArchiveManager.Models;

public sealed class DuplicateFolderItem
{
    public string FolderPath { get; init; } = "";
    public int ExactSetCount { get; init; }
    public int DuplicateFileCount { get; init; }
    public int RemovableFromFolderCount { get; init; }
    public long RemovableFromFolderBytes { get; init; }
    public int RemovableElsewhereCount { get; init; }
    public long RemovableElsewhereBytes { get; init; }
    public int OtherFolderCount { get; init; }
    public int InternalExtraCount { get; init; }
    public int RequiredKeeperCount { get; init; }
    public IReadOnlyList<DuplicateFolderMatchItem> Matches { get; init; } = Array.Empty<DuplicateFolderMatchItem>();

    public bool CanClearAllDuplicateFiles => DuplicateFileCount > 0 && RequiredKeeperCount == 0;

    public string HeaderText => FolderPath;
    public string SummaryText =>
        $"Наборов: {ExactSetCount:N0} · файлов-дублей здесь: {DuplicateFileCount:N0} · " +
        $"можно убрать отсюда: {RemovableFromFolderCount:N0} ({ByteFormatter.Format(RemovableFromFolderBytes)})";

    public string CounterpartText => OtherFolderCount == 0
        ? $"Совпадения только внутри этой папки · лишних внутри: {InternalExtraCount:N0}"
        : $"Копии есть ещё в {OtherFolderCount:N0} папк. · снаружи: {RemovableElsewhereCount:N0} ({ByteFormatter.Format(RemovableElsewhereBytes)})";

    public string ClearabilityText => CanClearAllDuplicateFiles
        ? "✓ Все найденные здесь точные дубли имеют копию в другой папке — их можно убрать отсюда целиком."
        : $"⚠ Для {RequiredKeeperCount:N0} наборов внешней копии нет — PAM обязательно оставит здесь по одному экземпляру.";

    public string RemoveFromFolderText =>
        $"Будет убрано из этой папки: {RemovableFromFolderCount:N0} · {ByteFormatter.Format(RemovableFromFolderBytes)}";

    public string KeepFolderText => RemovableElsewhereCount == 0
        ? "В других папках совпадающих копий нет."
        : $"В остальных папках будет убрано: {RemovableElsewhereCount:N0} · {ByteFormatter.Format(RemovableElsewhereBytes)}";
}

public sealed class DuplicateFolderMatchItem
{
    public string OtherFolderPath { get; init; } = "";
    public int SharedSetCount { get; init; }
    public int SelectedCopyCount { get; init; }
    public long SelectedBytes { get; init; }
    public int OtherCopyCount { get; init; }
    public long OtherBytes { get; init; }

    public string SummaryText =>
        $"Общих SHA-256: {SharedSetCount:N0} · здесь: {SelectedCopyCount:N0} ({ByteFormatter.Format(SelectedBytes)}) · " +
        $"там: {OtherCopyCount:N0} ({ByteFormatter.Format(OtherBytes)})";
}
