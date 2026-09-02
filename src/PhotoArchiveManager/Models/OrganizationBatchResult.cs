namespace PhotoArchiveManager.Models;

public sealed class OrganizationBatchResult
{
    public int Requested { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public long BytesMoved { get; set; }
    public List<string> Errors { get; } = new();
}
