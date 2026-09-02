namespace PhotoArchiveManager.Models;

public sealed class QuarantineBatchResult
{
    public int Requested { get; init; }
    public int Succeeded { get; set; }
    public long BytesMoved { get; set; }
    public List<string> Errors { get; } = new();
    public int Failed => Errors.Count;
}
