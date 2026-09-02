using OpenCvSharp;
using PhotoArchiveManager.Models;
using System.Runtime.InteropServices;

namespace PhotoArchiveManager.Services;

public sealed class FaceQualityAnalyzer
{
    private readonly FaceModelService _models;

    public FaceQualityAnalyzer(FaceModelService models) => _models = models;

    public FaceQualityMetrics AnalyzeGray(byte[] pixels, int width, int height, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _models.EnsureReady();

            using var gray = new Mat(height, width, MatType.CV_8UC1);
            Marshal.Copy(pixels, 0, gray.Data, pixels.Length);
            using var normalized = new Mat();
            Cv2.EqualizeHist(gray, normalized);

            using var faceCascade = new CascadeClassifier(_models.FaceCascadePath);
            using var eyeCascade = new CascadeClassifier(_models.EyeCascadePath);
            if (faceCascade.Empty() || eyeCascade.Empty())
                return Unavailable("OpenCV не смог открыть встроенные каскады лиц/глаз.");

            var minFace = Math.Max(32, Math.Min(width, height) / 18);
            var faces = faceCascade.DetectMultiScale(
                normalized,
                scaleFactor: 1.1,
                minNeighbors: 5,
                flags: HaarDetectionTypes.ScaleImage,
                minSize: new Size(minFace, minFace));

            cancellationToken.ThrowIfCancellationRequested();
            if (faces.Length == 0)
            {
                return new FaceQualityMetrics
                {
                    IsAvailable = true,
                    FaceCount = 0,
                    EyeCount = 0,
                    FaceSharpnessScore = -1,
                    EyeScore = -1,
                    Notes = "Лица не обнаружены; лицевые метрики не влияют на итоговый балл."
                };
            }

            var sharpness = new List<double>(faces.Length);
            var totalEyes = 0;
            var eyeCoverage = 0.0;

            foreach (var face in faces.OrderByDescending(x => x.Width * x.Height).Take(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safeFace = ClampRect(face, width, height);
                if (safeFace.Width < 20 || safeFace.Height < 20) continue;

                using (var faceRoi = new Mat(gray, safeFace))
                using (var lap = new Mat())
                {
                    Cv2.Laplacian(faceRoi, lap, MatType.CV_64F);
                    Cv2.MeanStdDev(lap, out _, out var stddev);
                    var variance = stddev.Val0 * stddev.Val0;
                    sharpness.Add(Clamp100(100.0 * (1.0 - Math.Exp(-variance / 320.0))));
                }

                // Eyes are normally in the upper part of a frontal face. The cascade detects
                // visible eye patterns; it is NOT a reliable "eyes open" classifier, so the UI
                // deliberately calls this eye visibility/detection rather than open-eye status.
                var upperHeight = Math.Max(1, (int)Math.Round(safeFace.Height * 0.68));
                var upper = new Rect(safeFace.X, safeFace.Y, safeFace.Width, upperHeight);
                using var eyeRegion = new Mat(normalized, upper);
                var minEye = Math.Max(8, safeFace.Width / 12);
                var maxEye = Math.Max(minEye + 1, safeFace.Width / 2);
                var eyes = eyeCascade.DetectMultiScale(
                    eyeRegion,
                    scaleFactor: 1.1,
                    minNeighbors: 4,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size(minEye, minEye),
                    maxSize: new Size(maxEye, maxEye));

                var plausible = eyes
                    .Where(e => e.Y + e.Height / 2.0 < upperHeight * 0.90)
                    .OrderByDescending(e => e.Width * e.Height)
                    .Take(2)
                    .Count();
                totalEyes += plausible;
                eyeCoverage += plausible / 2.0;
            }

            var countedFaces = Math.Min(faces.Length, 12);
            var faceSharpness = sharpness.Count == 0 ? -1 : sharpness.Average();
            var eyeScore = countedFaces <= 0 ? -1 : Clamp100(100.0 * eyeCoverage / countedFaces);
            var notes = $"Лиц: {faces.Length}; обнаружено глаз: {totalEyes}; " +
                        (faceSharpness >= 0 ? $"резкость лиц {faceSharpness:0}/100; " : "") +
                        (eyeScore >= 0 ? $"видимость глаз {eyeScore:0}/100." : "") +
                        " Детектор глаз эвристический и не гарантирует, что глаза открыты.";

            return new FaceQualityMetrics
            {
                IsAvailable = true,
                FaceCount = faces.Length,
                EyeCount = totalEyes,
                FaceSharpnessScore = faceSharpness,
                EyeScore = eyeScore,
                Notes = notes
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LoggingService.Error("Face/eye quality analysis failed", ex);
            return Unavailable("Лицевой анализ недоступен: " + ex.Message);
        }
    }

    private static Rect ClampRect(Rect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, x + 1, width);
        var bottom = Math.Clamp(rect.Bottom, y + 1, height);
        return new Rect(x, y, right - x, bottom - y);
    }

    private static FaceQualityMetrics Unavailable(string message) => new()
    {
        IsAvailable = false,
        FaceCount = -1,
        EyeCount = -1,
        FaceSharpnessScore = -1,
        EyeScore = -1,
        Notes = message
    };

    private static double Clamp100(double value) => Math.Clamp(value, 0, 100);
}
