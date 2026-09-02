namespace PhotoArchiveManager.Models;

public sealed class DuplicateGroupItem
{
    public string Sha256 { get; init; } = "";
    public long FileSize { get; init; }
    public IReadOnlyList<DuplicateFileItem> Files { get; init; } = Array.Empty<DuplicateFileItem>();

    public int FileCount => Files.Count;
    public long WastedBytes => FileCount > 1 ? FileSize * (FileCount - 1L) : 0;
    public string FileSizeDisplay => ByteFormatter.Format(FileSize);
    public string WastedDisplay => ByteFormatter.Format(WastedBytes);
    public string HashShort => Sha256.Length > 18 ? Sha256[..18] + "…" : Sha256;
    public string HeaderText => $"{FileCount:N0} одинаковых файла · {FileSizeDisplay}";
    public string SubheaderText => $"Лишних копий: {Math.Max(0, FileCount - 1):N0} · потенциально {WastedDisplay}";
}
