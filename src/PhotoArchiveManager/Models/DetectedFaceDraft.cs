namespace PhotoArchiveManager.Models;

public sealed class DetectedFaceDraft
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public double QualityScore { get; init; }
    public float[] Embedding { get; init; } = Array.Empty<float>();
    public string ThumbnailPath { get; init; } = "";
}
