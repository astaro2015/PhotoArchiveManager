namespace PhotoArchiveManager.Models;

public sealed class FaceQualityMetrics
{
    public bool IsAvailable { get; init; }
    public int FaceCount { get; init; } = -1;
    public int ImportantFaceCount { get; init; }
    public int EyeCount { get; init; } = -1;
    public double FaceSharpnessScore { get; init; } = -1;
    public double EyeScore { get; init; } = -1;
    public double EyeOpennessScore { get; init; } = -1;
    public int ReliableOpennessEyeCount { get; init; }
    public int ClosedEyeCount { get; init; } = -1;
    public double BlinkPenalty { get; init; }
    public double PoseScore { get; init; } = -1;
    public double WorstFaceScore { get; init; } = -1;
    public double AggregateFaceScore { get; init; } = -1;
    public double Importance { get; init; }
    public string Notes { get; init; } = "";
}
