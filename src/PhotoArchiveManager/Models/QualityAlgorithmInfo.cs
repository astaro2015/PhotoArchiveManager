namespace PhotoArchiveManager.Models;

public static class QualityAlgorithmInfo
{
    // Internal cache version. Increment whenever score semantics change so old cached
    // recommendations are never mixed with a newer Quality Score implementation.
    public const int CurrentVersion = 6;
    public const string DisplayName = "Quality Score v3.3";
}
