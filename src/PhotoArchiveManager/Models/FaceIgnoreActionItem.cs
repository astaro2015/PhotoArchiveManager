namespace PhotoArchiveManager.Models;

public sealed class FaceIgnoreActionItem
{
    public long Id { get; init; }
    public string ScopeType { get; init; } = "";
    public string ScopeLabel { get; init; } = "";
    public int FaceCount { get; init; }
    public string CreatedUtc { get; init; } = "";
    public string? UndoneUtc { get; init; }

    public bool IsActive => string.IsNullOrWhiteSpace(UndoneUtc);
    public string DisplayText => IsActive
        ? $"Можно восстановить: {ScopeLabel} · {FaceCount:N0} лиц"
        : "Последнее игнорирование уже восстановлено";
}
