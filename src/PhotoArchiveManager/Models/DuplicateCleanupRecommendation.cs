namespace PhotoArchiveManager.Models;

public sealed class DuplicateCleanupRecommendation
{
    public required DuplicateGroupItem Group { get; init; }
    public required DuplicateFileItem Keeper { get; init; }
    public string Header => Group.HeaderText;
    public string KeeperPath => Keeper.FullPath;
    public string Reason { get; init; } = "";
    public string Savings => Group.WastedDisplay;
}
