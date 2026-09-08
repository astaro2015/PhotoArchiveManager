namespace PhotoArchiveManager.Models;

public sealed class QualityMetrics
{
    public double TotalScore { get; init; }
    public double TechnicalScore { get; init; }
    public double SharpnessScore { get; init; }
    public double BlurScore { get; init; }
    public double ExposureScore { get; init; }
    public double ContrastScore { get; init; }
    public double NoiseScore { get; init; }
    public double ResolutionScore { get; init; }
    public double CompressionScore { get; init; }
    public double LaplacianVariance { get; init; }
    public double GradientStrength { get; init; }
    public double MeanLuminance { get; init; }
    public double DarkClipPercent { get; init; }
    public double BrightClipPercent { get; init; }

    public bool FaceAnalysisAvailable { get; init; }
    public int FaceCount { get; init; } = -1;
    public int EyeCount { get; init; } = -1;
    public double FaceScore { get; init; } = -1;
    public double EyeScore { get; init; } = -1;
    public double EyeOpennessScore { get; init; } = -1;
    public int ClosedEyeCount { get; init; } = -1;
    public double BlinkPenalty { get; init; }
    public double FacePoseScore { get; init; } = -1;
    public double WorstFaceScore { get; init; } = -1;

    public string Notes { get; init; } = "";
}
