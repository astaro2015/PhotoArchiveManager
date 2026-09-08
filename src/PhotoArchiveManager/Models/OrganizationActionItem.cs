using PhotoArchiveManager.Infrastructure;
namespace PhotoArchiveManager.Models;

public sealed class OrganizationActionItem
{
    public long Id { get; init; }
    public long FileId { get; init; }
    public string OriginalPath { get; init; } = "";
    public string NewPath { get; init; } = "";
    public string OriginalSourceFolder { get; init; } = "";
    public string NewSourceFolder { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long FileSize { get; init; }
    public long OriginalLastWriteUtcTicks { get; init; }
    public long OriginalCreationUtcTicks { get; init; }
    public string CreatedUtc { get; init; } = "";
    public string? UndoneUtc { get; init; }
    public string Error { get; init; } = "";

    public bool IsActive => string.IsNullOrWhiteSpace(UndoneUtc);
    public string FileName => Path.GetFileName(NewPath);
    public string FileSizeDisplay => ByteFormatter.Format(FileSize);
    public string StatusDisplay => IsActive ? "Перемещён · Undo доступен" : "Отменено";
    public string DateDisplay => StoredDateTime.TryParseRoundTripUtc(CreatedUtc, out var dt) ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : CreatedUtc;
}
