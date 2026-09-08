namespace PhotoArchiveManager.Models;

public sealed class ExactDeleteBatchResult
{
    public int Requested { get; init; }
    public int Succeeded { get; set; }
    public long BytesDeleted { get; set; }
    public List<string> Errors { get; } = new();
    public int Failed => Errors.Count;
}
