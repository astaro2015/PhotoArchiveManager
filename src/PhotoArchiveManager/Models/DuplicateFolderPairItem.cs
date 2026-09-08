namespace PhotoArchiveManager.Models;

public sealed class DuplicateFolderPairItem
{
    public string Sha256 { get; init; } = "";
    public long FileSize { get; init; }
    public int LeftCopyCount { get; init; }
    public int RightCopyCount { get; init; }
    public string LeftFilesText { get; init; } = "";
    public string RightFilesText { get; init; } = "";

    public string HashShort => Sha256.Length <= 12 ? Sha256 : Sha256[..12] + "…";
    public string SizeText => ByteFormatter.Format(FileSize);
    public long LeftBytes => FileSize * LeftCopyCount;
    public long RightBytes => FileSize * RightCopyCount;
    public string LeftCountText => $"Копий в A: {LeftCopyCount:N0}";
    public string RightCountText => $"Копий в B: {RightCopyCount:N0}";
}
