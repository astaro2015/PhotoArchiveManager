namespace PhotoArchiveManager.Models;

public sealed class QuarantineActionItem
{
    public long Id { get; init; }
    public long FileId { get; init; }
    public string OriginalPath { get; init; } = "";
    public string QuarantinePath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long FileSize { get; init; }
    public string CreatedUtc { get; init; } = "";
    public string? UndoneUtc { get; init; }
    public string? PermanentlyDeletedUtc { get; init; }
    public string Error { get; init; } = "";

    public bool IsPermanentlyDeleted => !string.IsNullOrWhiteSpace(PermanentlyDeletedUtc);
    public bool IsActive => string.IsNullOrWhiteSpace(UndoneUtc) && !IsPermanentlyDeleted;
    public string FileName => Path.GetFileName(OriginalPath);
    public string CurrentPath => IsActive ? QuarantinePath : IsPermanentlyDeleted ? "" : OriginalPath;
    public string FileSizeDisplay => ByteFormatter.Format(FileSize);
    public string CreatedDisplay => DateTime.TryParse(CreatedUtc, out var value)
        ? value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
        : CreatedUtc;
    public string StatusDisplay => IsPermanentlyDeleted ? "Удалён окончательно" : IsActive ? "В карантине" : "Восстановлен";
}
