namespace PhotoArchiveManager.Models;

public sealed class FaceQualityMetrics
{
    public bool IsAvailable { get; init; }
    public int FaceCount { get; init; } = -1;
    public int EyeCount { get; init; } = -1;
    public double FaceSharpnessScore { get; init; } = -1;
    public double EyeScore { get; init; } = -1;
    public string Notes { get; init; } = "";
}
