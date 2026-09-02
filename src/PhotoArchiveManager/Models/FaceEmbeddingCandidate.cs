namespace PhotoArchiveManager.Models;

public sealed class FaceEmbeddingCandidate
{
    public long FaceId { get; init; }
    public long FileId { get; init; }
    public long? PersonId { get; init; }
    public double QualityScore { get; init; }
    public float[] Embedding { get; init; } = Array.Empty<float>();
}
