namespace PhotoArchiveManager.Models;

public static class PerceptualHashAlgorithmInfo
{
    // v2: decode near working size + normalize all eight EXIF Orientation values.
    // The DB cache version prevents old full-decode/rotation-only hashes from being mixed with v2.
    public const int CurrentVersion = 2;
}
