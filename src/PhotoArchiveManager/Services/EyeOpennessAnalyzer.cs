using OpenCvSharp;

namespace PhotoArchiveManager.Services;

/// <summary>
/// Conservative, fully local eye-openness heuristic for Quality Score.
/// It uses only classic OpenCV operations around YuNet eye landmarks: no extra model,
/// no cloud/API and no network access. It intentionally returns Unknown often rather
/// than penalising glasses, squint, profile faces or low-resolution eyes as blinks.
/// </summary>
internal static class EyeOpennessAnalyzer
{
    private static int _firstFailureLogged;

    public static EyePairAssessment AnalyzePair(
        Mat gray,
        Point2f rightEye,
        Point2f leftEye,
        Rect face,
        double poseScore)
    {
        try
        {
            return AnalyzePairCore(gray, rightEye, leftEye, face, poseScore);
        }
        catch (Exception ex)
        {
            // Blink detection is an optional quality refinement. A local ROI/OpenCV edge case must
            // never make the whole photo's Quality Score fail. Log only the first failure to avoid
            // flooding Data\Logs on a large archive, then conservatively return Unknown/no penalty.
            if (Interlocked.Exchange(ref _firstFailureLogged, 1) == 0)
                LoggingService.Warn("Eye-openness heuristic disabled for an invalid eye ROI: " + ex.Message);
            return EyePairAssessment.Unknown;
        }
    }

    private static EyePairAssessment AnalyzePairCore(
        Mat gray,
        Point2f rightEye,
        Point2f leftEye,
        Rect face,
        double poseScore)
    {
        if (gray.Empty() || !IsFinite(rightEye) || !IsFinite(leftEye))
            return EyePairAssessment.Unknown;

        var dx = leftEye.X - rightEye.X;
        var dy = leftEye.Y - rightEye.Y;
        var eyeDistance = Math.Sqrt(dx * dx + dy * dy);
        var rollDegrees = Math.Abs(Math.Atan2(dy, dx) * 180.0 / Math.PI);

        // With only five YuNet landmarks we cannot correct strong yaw/pitch reliably.
        // Low-resolution and strongly rolled faces are therefore explicitly "unknown".
        if (eyeDistance < 28.0 || face.Width < 72 || face.Height < 72 || poseScore < 64.0 || rollDegrees > 16.0)
            return EyePairAssessment.Unknown;

        var right = AnalyzeEye(gray, rightEye, face, eyeDistance, poseScore, rollDegrees);
        var left = AnalyzeEye(gray, leftEye, face, eyeDistance, poseScore, rollDegrees);
        var reliable = new[] { right, left }.Where(x => x.State != EyeState.Unknown).ToArray();
        if (reliable.Length == 0)
            return EyePairAssessment.Unknown;

        var closed = reliable.Where(x => x.State == EyeState.ProbablyClosed).ToArray();
        var open = reliable.Where(x => x.State == EyeState.ProbablyOpen).ToArray();
        var openness = reliable.Average(x => x.OpennessScore);

        double blinkPenalty;
        double closedConfidence;
        if (closed.Length >= 2)
        {
            var scoreGap = Math.Abs(closed[0].OpennessScore - closed[1].OpennessScore);
            // A real blink usually affects both eyes similarly. Large asymmetry is more likely
            // to be shadow, glasses, occlusion or a landmark/ROI oddity, so reduce confidence
            // rather than boosting a merely double-positive classification.
            var pairConsistency = 0.72 + 0.28 * Clamp01((26.0 - scoreGap) / 26.0);
            closedConfidence = Clamp01(closed.Average(x => x.Confidence) * pairConsistency);
            blinkPenalty = 10.0 * closedConfidence;
        }
        else if (closed.Length == 1)
        {
            closedConfidence = closed[0].Confidence;
            // One closed eye may be a wink, partial blink or occlusion. Keep the penalty modest.
            // A confidently open second eye makes this less like a normal blink; an unknown second
            // eye is also uncertain. Neither case is allowed to approach the two-eye penalty.
            var otherEyeFactor = open.Length > 0 ? 0.45 : 0.35;
            blinkPenalty = 4.0 * closedConfidence * otherEyeFactor;
        }
        else
        {
            closedConfidence = 0;
            blinkPenalty = 0;
        }

        return new EyePairAssessment(
            ReliableEyeCount: reliable.Length,
            ClosedEyeCount: closed.Length,
            OpennessScore: openness,
            ClosedConfidence: closedConfidence,
            BlinkPenalty: blinkPenalty,
            Right: right,
            Left: left);
    }

    private static EyeAssessment AnalyzeEye(
        Mat gray,
        Point2f eye,
        Rect face,
        double eyeDistance,
        double poseScore,
        double rollDegrees)
    {
        var desiredWidth = Math.Max(18, (int)Math.Round(eyeDistance * 0.62));
        var desiredHeight = Math.Max(12, (int)Math.Round(eyeDistance * 0.42));
        desiredWidth = Math.Min(desiredWidth, Math.Max(18, (int)Math.Round(face.Width * 0.42)));
        desiredHeight = Math.Min(desiredHeight, Math.Max(12, (int)Math.Round(face.Height * 0.28)));

        var requested = new Rect(
            (int)Math.Round(eye.X - desiredWidth / 2.0),
            (int)Math.Round(eye.Y - desiredHeight / 2.0),
            desiredWidth,
            desiredHeight);
        var rect = Intersect(Intersect(requested, face), new Rect(0, 0, gray.Width, gray.Height));
        if (rect.Width < 16 || rect.Height < 10 ||
            rect.Width < desiredWidth * 0.82 || rect.Height < desiredHeight * 0.82)
            return EyeAssessment.Unknown;

        using var roi = new Mat(gray, rect);
        using var smooth = new Mat();
        Cv2.GaussianBlur(roi, smooth, new Size(3, 3), 0.8);
        Cv2.MeanStdDev(smooth, out var meanScalar, out var stdScalar);
        var mean = meanScalar.Val0;
        var stddev = stdScalar.Val0;
        if (!double.IsFinite(stddev) || stddev < 7.5)
            return EyeAssessment.Unknown;

        using var sobelX = new Mat();
        using var sobelY = new Mat();
        Cv2.Sobel(smooth, sobelX, MatType.CV_32FC1, 1, 0, 3);
        Cv2.Sobel(smooth, sobelY, MatType.CV_32FC1, 0, 1, 3);

        var x0 = Math.Clamp((int)Math.Round(rect.Width * 0.12), 0, rect.Width - 1);
        var x1 = Math.Clamp((int)Math.Round(rect.Width * 0.88), x0 + 1, rect.Width);
        var y0 = Math.Clamp((int)Math.Round(rect.Height * 0.18), 0, rect.Height - 1);
        var y1 = Math.Clamp((int)Math.Round(rect.Height * 0.82), y0 + 1, rect.Height);
        var regionWidth = Math.Max(1, x1 - x0);
        var regionHeight = Math.Max(1, y1 - y0);

        var darkThreshold = Math.Clamp(mean - stddev * 0.55, 18.0, 205.0);
        var rowDark = new int[regionHeight];
        var longestDarkRunByColumn = new int[regionWidth];
        double sumAbsX = 0, sumAbsY = 0;
        long sampleCount = 0, darkCount = 0;
        long centerDark = 0, centerSamples = 0, sideDark = 0, sideSamples = 0;

        for (var x = x0; x < x1; x++)
        {
            var currentRun = 0;
            var longestRun = 0;
            for (var y = y0; y < y1; y++)
            {
                var ax = Math.Abs(sobelX.At<float>(y, x));
                var ay = Math.Abs(sobelY.At<float>(y, x));
                sumAbsX += ax;
                sumAbsY += ay;
                sampleCount++;

                var isDark = smooth.At<byte>(y, x) <= darkThreshold;
                if (isDark)
                {
                    darkCount++;
                    rowDark[y - y0]++;
                    currentRun++;
                    if (currentRun > longestRun) longestRun = currentRun;
                }
                else
                {
                    currentRun = 0;
                }

                var nx = (x - x0 + 0.5) / regionWidth;
                if (nx >= 0.30 && nx <= 0.70)
                {
                    centerSamples++;
                    if (isDark) centerDark++;
                }
                else if (nx <= 0.22 || nx >= 0.78)
                {
                    sideSamples++;
                    if (isDark) sideDark++;
                }
            }
            longestDarkRunByColumn[x - x0] = longestRun;
        }

        if (sampleCount <= 0)
            return EyeAssessment.Unknown;

        var edgeEnergy = (sumAbsX + sumAbsY) / sampleCount;
        if (edgeEnergy < 6.0)
            return EyeAssessment.Unknown;

        var verticalEdgeShare = sumAbsX / Math.Max(1e-6, sumAbsX + sumAbsY);
        var activeThreshold = Math.Max(1, (int)Math.Ceiling(regionWidth * 0.09));
        var longestActiveRows = LongestRun(rowDark, x => x >= activeThreshold);
        var darkThickness = longestActiveRows / (double)regionHeight;

        Array.Sort(longestDarkRunByColumn);
        var runIndex = Math.Clamp((int)Math.Round((longestDarkRunByColumn.Length - 1) * 0.75), 0, longestDarkRunByColumn.Length - 1);
        var verticalDarkRun = longestDarkRunByColumn[runIndex] / (double)regionHeight;

        var centerDarkFraction = centerSamples > 0 ? centerDark / (double)centerSamples : 0;
        var sideDarkFraction = sideSamples > 0 ? sideDark / (double)sideSamples : 0;
        var centerDarkAdvantage = centerDarkFraction - sideDarkFraction;
        var lineConcentration = darkCount > 0 ? rowDark.Max() / (double)darkCount : 0;

        // Open eyes usually preserve vertical iris/pupil structure (Sobel-X), a taller central
        // dark run and a centre that is darker than the lateral sclera/skin. A closed eyelid tends
        // to collapse into a comparatively thin horizontal structure. The ranges are intentionally
        // broad; ambiguous cases remain Unknown instead of receiving a blink penalty.
        var edgeEvidence = Scale01(verticalEdgeShare, 0.22, 0.46);
        var runEvidence = Scale01(verticalDarkRun, 0.18, 0.58);
        var centreEvidence = Scale01(centerDarkAdvantage, -0.02, 0.16);
        var thicknessEvidence = Scale01(darkThickness, 0.20, 0.60);
        var linePenalty = Scale01(lineConcentration, 0.18, 0.48);
        var openness = Clamp100((
            edgeEvidence * 0.45 +
            runEvidence * 0.30 +
            centreEvidence * 0.15 +
            thicknessEvidence * 0.10) * 100.0 - linePenalty * 12.0);

        var resolutionReliability = Scale01(eyeDistance, 28.0, 56.0);
        var contrastReliability = Scale01(stddev, 8.5, 30.0);
        var edgeReliability = Scale01(edgeEnergy, 6.0, 23.0);
        var poseReliability = Scale01(poseScore, 64.0, 91.0) * (1.0 - 0.25 * Scale01(rollDegrees, 8.0, 16.0));
        var reliability = Clamp01(
            resolutionReliability * 0.27 +
            contrastReliability * 0.25 +
            edgeReliability * 0.20 +
            poseReliability * 0.28);

        var closedShape = verticalDarkRun <= 0.52 && verticalEdgeShare <= 0.31 && lineConcentration >= 0.105;
        if (openness <= 34.0 && reliability >= 0.64 && closedShape)
        {
            var separation = Scale01(38.0 - openness, 4.0, 25.0);
            return new EyeAssessment(EyeState.ProbablyClosed, openness, Clamp01(reliability * (0.80 + 0.20 * separation)));
        }

        if (openness >= 60.0 && reliability >= 0.50)
        {
            var separation = Scale01(openness - 55.0, 5.0, 30.0);
            return new EyeAssessment(EyeState.ProbablyOpen, openness, Clamp01(reliability * (0.82 + 0.18 * separation)));
        }

        return new EyeAssessment(EyeState.Unknown, openness, reliability * 0.55);
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);
        return x2 <= x1 || y2 <= y1 ? new Rect() : new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    private static int LongestRun<T>(IReadOnlyList<T> values, Func<T, bool> predicate)
    {
        var best = 0;
        var current = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (predicate(values[i]))
            {
                current++;
                if (current > best) best = current;
            }
            else
            {
                current = 0;
            }
        }
        return best;
    }

    private static bool IsFinite(Point2f p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static double Scale01(double value, double low, double high)
        => high <= low ? 0 : Clamp01((value - low) / (high - low));
    private static double Clamp100(double value) => Math.Clamp(value, 0, 100);
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}

internal enum EyeState
{
    Unknown = 0,
    ProbablyOpen = 1,
    ProbablyClosed = 2
}

internal readonly record struct EyeAssessment(EyeState State, double OpennessScore, double Confidence)
{
    public static EyeAssessment Unknown => new(EyeState.Unknown, -1, 0);
}

internal readonly record struct EyePairAssessment(
    int ReliableEyeCount,
    int ClosedEyeCount,
    double OpennessScore,
    double ClosedConfidence,
    double BlinkPenalty,
    EyeAssessment Right,
    EyeAssessment Left)
{
    public static EyePairAssessment Unknown => new(0, 0, -1, 0, 0, EyeAssessment.Unknown, EyeAssessment.Unknown);
}
