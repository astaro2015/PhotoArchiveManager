using OpenCvSharp;
using OpenCvSharp.Dnn;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class FaceQualityAnalyzer
{
    private readonly FaceModelService _models;

    public FaceQualityAnalyzer(FaceModelService models) => _models = models;

    /// <summary>
    /// Fully local face-quality analysis. Uses the same embedded YuNet model as the People index.
    /// No network/API calls are performed. EyeScore remains local eye-region detail/visibility.
    /// Quality Score v3.3 additionally applies a conservative classic-OpenCV eye-openness heuristic
    /// only to main faces. It is intentionally biased toward Unknown on ambiguous, low-resolution
    /// or strongly non-frontal cases instead of forcing a closed-eye verdict.
    /// </summary>
    public FaceQualityMetrics AnalyzeBgr(Mat bgr, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCvRuntimeDiagnostics.EnsureAvailable();
            _models.EnsureYuNetReady();

            if (bgr.Empty())
                throw new InvalidDataException("Пустое изображение передано в YuNet.");
            if (bgr.Width < 24 || bgr.Height < 24)
            {
                return new FaceQualityMetrics
                {
                    IsAvailable = true,
                    FaceCount = 0,
                    ImportantFaceCount = 0,
                    EyeCount = 0,
                    EyeOpennessScore = -1,
                    ReliableOpennessEyeCount = 0,
                    ClosedEyeCount = 0,
                    BlinkPenalty = 0,
                    FaceSharpnessScore = -1,
                    EyeScore = -1,
                    PoseScore = -1,
                    WorstFaceScore = -1,
                    AggregateFaceScore = -1,
                    Importance = 0,
                    Notes = "Кадр слишком мал для содержательного поиска лиц; лицевые метрики не влияют на итоговый балл."
                };
            }

            using var detector = FaceDetectorYN.Create(
                _models.YuNetModelPath, "", bgr.Size(),
                scoreThreshold: 0.76f, nmsThreshold: 0.30f, topK: 5000,
                backendId: Backend.OPENCV, targetId: Target.CPU);
            using var detections = new Mat();
            detector.Detect(bgr, detections);

            cancellationToken.ThrowIfCancellationRequested();
            if (detections.Empty() || detections.Rows <= 0)
            {
                return new FaceQualityMetrics
                {
                    IsAvailable = true,
                    FaceCount = 0,
                    ImportantFaceCount = 0,
                    EyeCount = 0,
                    EyeOpennessScore = -1,
                    ReliableOpennessEyeCount = 0,
                    ClosedEyeCount = 0,
                    BlinkPenalty = 0,
                    FaceSharpnessScore = -1,
                    EyeScore = -1,
                    PoseScore = -1,
                    WorstFaceScore = -1,
                    AggregateFaceScore = -1,
                    Importance = 0,
                    Notes = "YuNet: лица не обнаружены; лицевые метрики не влияют на итоговый балл."
                };
            }

            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

            var imageArea = Math.Max(1.0, bgr.Width * (double)bgr.Height);
            var faces = new List<FaceMeasure>();
            var rows = Math.Min(detections.Rows, 24);

            for (var i = 0; i < rows; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (detections.Cols < 15) break;

                var confidence = detections.At<float>(i, 14);
                if (!float.IsFinite(confidence) || confidence < 0.76f) continue;

                var raw = new Rect(
                    (int)Math.Floor(detections.At<float>(i, 0)),
                    (int)Math.Floor(detections.At<float>(i, 1)),
                    Math.Max(1, (int)Math.Ceiling(detections.At<float>(i, 2))),
                    Math.Max(1, (int)Math.Ceiling(detections.At<float>(i, 3))));
                var rect = ClipRect(raw, bgr.Width, bgr.Height);
                if (rect.Width < 20 || rect.Height < 20) continue;

                // OpenCV FaceDetectorYN layout is: subject's right eye, subject's left eye,
                // nose, right mouth corner, left mouth corner. Keep the names exact so later
                // pose changes do not accidentally invert the landmark semantics.
                var rightEye = ReadPoint(detections, i, 4, 5);
                var leftEye = ReadPoint(detections, i, 6, 7);
                var nose = ReadPoint(detections, i, 8, 9);
                var mouthRight = ReadPoint(detections, i, 10, 11);
                var mouthLeft = ReadPoint(detections, i, 12, 13);

                var faceSharpness = ComputeRegionSharpness(gray, rect, 330.0);
                var eyesValid = IsPlausibleEyePair(rightEye, leftEye, rect);
                var eyeDetail = -1.0;
                if (eyesValid)
                {
                    var leftDetail = ComputeEyeRegionSharpness(gray, leftEye, rect);
                    var rightDetail = ComputeEyeRegionSharpness(gray, rightEye, rect);
                    if (leftDetail >= 0 && rightDetail >= 0)
                    {
                        eyeDetail = (leftDetail + rightDetail) / 2.0;
                    }
                }

                var pose = ComputePoseScore(rightEye, leftEye, nose, mouthRight, mouthLeft, rect);
                var areaRatio = rect.Width * (double)rect.Height / imageArea;
                var sizeScore = Clamp100(28.0 + 72.0 * Math.Sqrt(Math.Max(0, areaRatio) / 0.04));
                var borderScore = ComputeBorderScore(rect, bgr.Width, bgr.Height);
                var confidenceScore = Clamp100((confidence - 0.70) / 0.28 * 100.0);
                var eyeComponent = eyeDetail >= 0 ? eyeDetail : Math.Min(faceSharpness, 62.0);

                var total = Clamp100(
                    faceSharpness * 0.52 +
                    eyeComponent * 0.15 +
                    pose * 0.18 +
                    sizeScore * 0.05 +
                    borderScore * 0.05 +
                    confidenceScore * 0.05);

                faces.Add(new FaceMeasure(
                    Area: rect.Width * (double)rect.Height,
                    AreaRatio: areaRatio,
                    Rect: rect,
                    RightEye: rightEye,
                    LeftEye: leftEye,
                    Sharpness: faceSharpness,
                    EyeDetail: eyeDetail,
                    Pose: pose,
                    Total: total,
                    EyeOpenness: -1,
                    ReliableOpennessEyeCount: 0,
                    ClosedEyeCount: 0,
                    BlinkPenalty: 0));
            }

            if (faces.Count == 0)
            {
                return new FaceQualityMetrics
                {
                    IsAvailable = true,
                    FaceCount = 0,
                    ImportantFaceCount = 0,
                    EyeCount = 0,
                    EyeOpennessScore = -1,
                    ReliableOpennessEyeCount = 0,
                    ClosedEyeCount = 0,
                    BlinkPenalty = 0,
                    FaceSharpnessScore = -1,
                    EyeScore = -1,
                    PoseScore = -1,
                    WorstFaceScore = -1,
                    AggregateFaceScore = -1,
                    Importance = 0,
                    Notes = "YuNet вернул кандидатов, но надёжных лиц после проверки геометрии не осталось."
                };
            }

            var maxArea = faces.Max(x => x.Area);
            // Main faces are selected by both relative and absolute visual size. A pure 25%-of-largest
            // rule can wrongly drop a still-important person when somebody stands much closer to the
            // camera. The absolute branch keeps reasonably sized group members, while EyeOpennessAnalyzer
            // still refuses low-resolution faces (<72 px) so background micro-faces cannot create blinks.
            var important = faces
                .Where(x => x.Area >= maxArea * 0.18 || x.AreaRatio >= 0.0015)
                .OrderByDescending(x => x.Area)
                .ToList();
            if (important.Count == 0) important = faces.OrderByDescending(x => x.Area).Take(1).ToList();

            // Closed-eye/blink heuristics are intentionally evaluated ONLY for main faces.
            // Background/micro faces therefore cannot receive a blink penalty even if YuNet found them.
            for (var i = 0; i < important.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = important[i];
                var openness = EyeOpennessAnalyzer.AnalyzePair(
                    gray, current.RightEye, current.LeftEye, current.Rect, current.Pose);

                // Keep this penalty inside face quality modest; QualityAnalyzer also applies the
                // dedicated conservative BlinkPenalty so a high-confidence blink matters in a burst.
                // Reuse the pair-level confidence/one-eye conservatism instead of applying a second,
                // unrelated one-eye penalty. Two confidently closed eyes can still reduce a face by
                // up to 18 points; a wink/one-eye case remains much softer.
                var facePenalty = openness.BlinkPenalty * 1.80;

                important[i] = current with
                {
                    Total = Clamp100(current.Total - facePenalty),
                    EyeOpenness = openness.OpennessScore,
                    ReliableOpennessEyeCount = openness.ReliableEyeCount,
                    ClosedEyeCount = openness.ClosedEyeCount,
                    BlinkPenalty = openness.BlinkPenalty
                };
            }

            var weightedTotal = WeightedAverage(important, x => x.Total);
            var weightedSharpness = WeightedAverage(important, x => x.Sharpness);
            var weightedPose = WeightedAverage(important, x => x.Pose);
            var eyeFaces = important.Where(x => x.EyeDetail >= 0).ToList();
            var importantEyeCount = eyeFaces.Count * 2;
            var weightedEyes = eyeFaces.Count == 0 ? -1 : WeightedAverage(eyeFaces, x => x.EyeDetail);
            var opennessFaces = important.Where(x => x.EyeOpenness >= 0).ToList();
            var weightedOpenness = opennessFaces.Count == 0 ? -1 : WeightedAverage(opennessFaces, x => x.EyeOpenness);
            var reliableOpennessEyes = important.Sum(x => x.ReliableOpennessEyeCount);
            var closedEyeCount = important.Sum(x => x.ClosedEyeCount);
            var blinkFaceCount = important.Count(x => x.ClosedEyeCount > 0);
            var blinkPenalties = important.Select(x => x.BlinkPenalty).Where(x => x > 0).OrderByDescending(x => x).ToArray();
            var blinkPenalty = blinkPenalties.Length == 0
                ? 0.0
                : Math.Min(12.0, blinkPenalties[0] + blinkPenalties.Skip(1).Sum() * 0.30);
            var worst = important.Min(x => x.Total);

            // Do not let several good faces completely hide one poor main face in a group shot.
            var aggregate = Clamp100(weightedTotal * 0.72 + worst * 0.28);
            var largestRatio = faces.Max(x => x.AreaRatio);
            var importance = ComputeFaceImportance(largestRatio, important.Count);

            var blinkNote = reliableOpennessEyes == 0
                ? "открытость глаз: недостаточно надёжных данных; "
                : closedEyeCount > 0
                    ? $"вероятно закрытых глаз {closedEyeCount} на {blinkFaceCount} основном лице(ах), открытость {weightedOpenness:0}/100; "
                    : $"надёжно проверено глаз {reliableOpennessEyes}, вероятно закрытых не найдено, открытость {weightedOpenness:0}/100; ";

            var notes = $"YuNet: лиц {faces.Count}; основных {important.Count}; " +
                        $"резкость лиц {weightedSharpness:0}/100; поза {weightedPose:0}/100; " +
                        (weightedEyes >= 0 ? $"детализация глаз {weightedEyes:0}/100; " : "глазные области ненадёжны; ") +
                        blinkNote +
                        $"худшее основное лицо {worst:0}/100. " +
                        "Закрытые глаза определяются консервативной локальной OpenCV-эвристикой без отдельной AI-модели; сомнительные случаи не штрафуются.";

            return new FaceQualityMetrics
            {
                IsAvailable = true,
                FaceCount = faces.Count,
                ImportantFaceCount = important.Count,
                EyeCount = importantEyeCount,
                FaceSharpnessScore = weightedSharpness,
                EyeScore = weightedEyes,
                EyeOpennessScore = weightedOpenness,
                ReliableOpennessEyeCount = reliableOpennessEyes,
                ClosedEyeCount = closedEyeCount,
                BlinkPenalty = blinkPenalty,
                PoseScore = weightedPose,
                WorstFaceScore = worst,
                AggregateFaceScore = aggregate,
                Importance = importance,
                Notes = notes
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LoggingService.Error("YuNet face-quality analysis failed", ex);
            // Do not silently cache a technical-only result as a complete Quality Score.
            // If YuNet/model extraction is temporarily broken, the whole quality record must stay
            // retryable; otherwise fixing the runtime later would never refresh face-aware scores.
            throw new InvalidOperationException("Лицевой анализ YuNet не выполнен: " + ex.Message, ex);
        }
    }

    private static Point2f ReadPoint(Mat detections, int row, int xCol, int yCol)
        => new(detections.At<float>(row, xCol), detections.At<float>(row, yCol));

    private static bool IsPlausibleEyePair(Point2f rightEye, Point2f leftEye, Rect face)
    {
        if (!IsFinite(rightEye) || !IsFinite(leftEye)) return false;
        if (!Contains(face, rightEye) || !Contains(face, leftEye)) return false;
        var distance = Distance(rightEye, leftEye);
        return distance >= face.Width * 0.16 && distance <= face.Width * 0.86;
    }

    private static double ComputePoseScore(
        Point2f rightEye, Point2f leftEye, Point2f nose,
        Point2f mouthRight, Point2f mouthLeft, Rect face)
    {
        if (!IsPlausibleEyePair(rightEye, leftEye, face) ||
            !IsFinite(nose) || !IsFinite(mouthLeft) || !IsFinite(mouthRight))
            return 55.0;

        var eyeDistance = Math.Max(1.0, Distance(rightEye, leftEye));
        var eyeMidX = (rightEye.X + leftEye.X) / 2.0;
        var eyeMidY = (rightEye.Y + leftEye.Y) / 2.0;
        var mouthMidX = (mouthRight.X + mouthLeft.X) / 2.0;
        var mouthMidY = (mouthRight.Y + mouthLeft.Y) / 2.0;

        var noseHorizontal = Math.Abs(nose.X - eyeMidX) / eyeDistance;
        var mouthHorizontal = Math.Abs(mouthMidX - eyeMidX) / eyeDistance;
        var rollDegrees = Math.Abs(Math.Atan2(leftEye.Y - rightEye.Y, leftEye.X - rightEye.X) * 180.0 / Math.PI);

        var eyeToMouth = Math.Max(1.0, mouthMidY - eyeMidY);
        var noseVertical = (nose.Y - eyeMidY) / eyeToMouth;
        var verticalPenalty = Math.Abs(noseVertical - 0.50);

        var yawPenalty = Clamp01((noseHorizontal - 0.07) / 0.38) * 52.0;
        var mouthPenalty = Clamp01((mouthHorizontal - 0.08) / 0.32) * 15.0;
        var rollPenalty = Clamp01((rollDegrees - 13.0) / 32.0) * 18.0;
        var geometryPenalty = Clamp01((verticalPenalty - 0.12) / 0.30) * 15.0;
        return Clamp100(100.0 - yawPenalty - mouthPenalty - rollPenalty - geometryPenalty);
    }

    private static double ComputeEyeRegionSharpness(Mat gray, Point2f eye, Rect face)
    {
        if (!IsFinite(eye)) return -1;
        var w = Math.Max(10, (int)Math.Round(face.Width * 0.22));
        var h = Math.Max(8, (int)Math.Round(face.Height * 0.16));
        var rect = ClipRect(new Rect(
            (int)Math.Round(eye.X - w / 2.0),
            (int)Math.Round(eye.Y - h / 2.0), w, h), gray.Width, gray.Height);
        if (rect.Width < 8 || rect.Height < 6) return -1;
        return ComputeRegionSharpness(gray, rect, 210.0);
    }

    private static double ComputeRegionSharpness(Mat gray, Rect rect, double scale)
    {
        using var roi = new Mat(gray, rect);
        using var lap = new Mat();
        Cv2.Laplacian(roi, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var stddev);
        var variance = Math.Max(0, stddev.Val0 * stddev.Val0);
        return Clamp100(100.0 * (1.0 - Math.Exp(-variance / scale)));
    }

    private static double ComputeBorderScore(Rect rect, int width, int height)
    {
        var margin = Math.Max(2.0, Math.Min(width, height) * 0.008);
        var left = rect.X;
        var top = rect.Y;
        var right = width - rect.Right;
        var bottom = height - rect.Bottom;
        var nearest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        if (nearest <= 1) return 45;
        if (nearest < margin) return 72;
        return 100;
    }

    private static double ComputeFaceImportance(double largestAreaRatio, int importantFaceCount)
    {
        // Roughly 0 below a tiny incidental face and 1 around a classic portrait-size face.
        var lo = Math.Sqrt(0.0015);
        var hi = Math.Sqrt(0.055);
        var normalized = Clamp01((Math.Sqrt(Math.Max(0, largestAreaRatio)) - lo) / (hi - lo));
        if (importantFaceCount >= 2 && normalized > 0) normalized = Math.Min(1.0, normalized + 0.08);
        return normalized;
    }

    private static double WeightedAverage(IReadOnlyList<FaceMeasure> faces, Func<FaceMeasure, double> selector)
    {
        double sum = 0, weightSum = 0;
        foreach (var face in faces)
        {
            var weight = Math.Sqrt(Math.Max(1.0, face.Area));
            sum += selector(face) * weight;
            weightSum += weight;
        }
        return weightSum <= 0 ? 0 : sum / weightSum;
    }

    private static Rect ClipRect(Rect source, int width, int height)
    {
        var x1 = Math.Clamp(source.X, 0, Math.Max(0, width - 1));
        var y1 = Math.Clamp(source.Y, 0, Math.Max(0, height - 1));
        var x2 = Math.Clamp(source.Right, x1 + 1, width);
        var y2 = Math.Clamp(source.Bottom, y1 + 1, height);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    private static bool Contains(Rect rect, Point2f p)
        => p.X >= rect.X && p.X <= rect.Right && p.Y >= rect.Y && p.Y <= rect.Bottom;

    private static bool IsFinite(Point2f p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static double Distance(Point2f a, Point2f b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Clamp100(double value) => Math.Clamp(value, 0, 100);
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private sealed record FaceMeasure(
        double Area,
        double AreaRatio,
        Rect Rect,
        Point2f RightEye,
        Point2f LeftEye,
        double Sharpness,
        double EyeDetail,
        double Pose,
        double Total,
        double EyeOpenness,
        int ReliableOpennessEyeCount,
        int ClosedEyeCount,
        double BlinkPenalty);
}
