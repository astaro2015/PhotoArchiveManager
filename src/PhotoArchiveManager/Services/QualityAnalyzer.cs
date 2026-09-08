using Microsoft.Data.Sqlite;
using OpenCvSharp;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;
using System.Runtime.InteropServices;

namespace PhotoArchiveManager.Services;

public sealed class QualityAnalyzer
{
    public const int AlgorithmVersion = QualityAlgorithmInfo.CurrentVersion;

    private readonly DatabaseService _database;
    private readonly FaceQualityAnalyzer _faceQualityAnalyzer;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _internalCts;

    private static readonly int[] StandardLuminanceQuantization =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    ];

    // JPEG DQT tables are serialized in zig-zag order.
    private static readonly int[] ZigZagOrder =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    ];

    public bool IsPaused => _pauseGate.IsPaused;

    public QualityAnalyzer(DatabaseService database, FaceQualityAnalyzer faceQualityAnalyzer)
    {
        _database = database;
        _faceQualityAnalyzer = faceQualityAnalyzer;
    }

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _internalCts?.Cancel();

    public async Task AnalyzeGroupAsync(
        IReadOnlyList<VisualDuplicateFileItem> files,
        IProgress<QualityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) return;

        _internalCts?.Dispose();
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _internalCts.Token;
        _pauseGate.Resume();

        var processed = 0;
        var computed = 0;
        var cached = 0;
        var errors = 0;

        try
        {
            await using var connection = _database.CreateConnection();
            await connection.OpenAsync(token);

            foreach (var item in files)
            {
                token.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(token);

                if (!item.HasQuality || processed % 25 == 0)
                {
                    progress?.Report(new QualityProgress
                    {
                        Stage = item.HasQuality ? QualityAlgorithmInfo.DisplayName + " — из кэша" : QualityAlgorithmInfo.DisplayName,
                        CurrentFile = item.FullPath,
                        TotalFiles = files.Count,
                        ProcessedFiles = processed,
                        ComputedFiles = computed,
                        CachedFiles = cached,
                        ErrorFiles = errors
                    });
                }

                if (item.HasQuality)
                {
                    cached++;
                    processed++;
                    continue;
                }

                try
                {
                    var info = new FileInfo(item.FullPath);
                    if (!info.Exists)
                        throw new FileNotFoundException("Файл не найден.", item.FullPath);
                    if (info.Length != item.FileSize || info.LastWriteTimeUtc.Ticks != item.LastWriteUtcTicks)
                        throw new IOException("Файл изменился после индексации. Выполните повторное сканирование библиотеки.");

                    var metrics = await Task.Run(() => ComputeMetrics(item, token), token);
                    var after = new FileInfo(item.FullPath);
                    if (!after.Exists || after.Length != item.FileSize || after.LastWriteTimeUtc.Ticks != item.LastWriteUtcTicks)
                        throw new IOException("Файл изменился во время оценки качества. Выполните повторное сканирование библиотеки.");
                    await _database.UpdateQualityAsync(connection, item.Id, item.FileSize, item.LastWriteUtcTicks, metrics, "", token);
                    computed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    LoggingService.Error("Quality v3.3 analysis failed: " + item.FullPath, ex);
                    await _database.UpdateQualityAsync(connection, item.Id, item.FileSize, item.LastWriteUtcTicks, null, ex.Message, token);
                }

                processed++;
            }

            progress?.Report(new QualityProgress
            {
                Stage = QualityAlgorithmInfo.DisplayName + " завершён",
                TotalFiles = files.Count,
                ProcessedFiles = processed,
                ComputedFiles = computed,
                CachedFiles = cached,
                ErrorFiles = errors
            });
        }
        finally
        {
            _internalCts?.Dispose();
            _internalCts = null;
        }
    }

    private QualityMetrics ComputeMetrics(VisualDuplicateFileItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OpenCvRuntimeDiagnostics.EnsureAvailable();

        // Keep technical metrics calibrated at 1200 px, but preserve more detail for YuNet.
        // A group photo can contain perfectly important faces that become <20 px at 1200 px
        // long edge; detecting faces from 1800 px noticeably reduces that blind spot without
        // changing the technical-score calibration.
        using var faceBgr = ImageMatLoader.LoadBgr(item.FullPath, item.Orientation, 1800);
        if (faceBgr.Empty() || faceBgr.Width < 5 || faceBgr.Height < 5)
            throw new InvalidDataException("Изображение слишком маленькое для оценки качества.");
        using var bgr = ResizeForTechnicalAnalysis(faceBgr, 1200);

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        var width = gray.Width;
        var height = gray.Height;
        var pixels = CopyGrayPixels(gray);

        var histogram = new long[256];
        long luminanceSum = 0;
        foreach (var p in pixels)
        {
            histogram[p]++;
            luminanceSum += p;
        }

        token.ThrowIfCancellationRequested();
        var level1 = ComputeDetailStats(pixels, width, height, token);
        var half = Downsample2x(pixels, width, height, out var halfWidth, out var halfHeight);
        var level2 = halfWidth >= 5 && halfHeight >= 5
            ? ComputeDetailStats(half, halfWidth, halfHeight, token)
            : level1;

        // v3 measures edge ACUTANCE rather than simply counting high-frequency energy.
        // This is crucial: sensor noise raises Laplacian variance, while a real blurred edge
        // loses second-derivative strength relative to its first-derivative contrast.
        var noiseEstimate = Math.Max(0, level1.FlatResidual);
        var noiseScore = ComputeNoiseScore(noiseEstimate);
        var acuity1 = level1.EdgeFraction < 0.006 ? 78.0 : MapExp(level1.EdgeAcuity, 0.14);
        var acuity2 = level2.EdgeFraction < 0.006 ? 78.0 : MapExp(level2.EdgeAcuity, 0.14);
        var sharpnessRaw = Clamp100(acuity1 * 0.68 + acuity2 * 0.32);
        var sharpnessScore = Clamp100(sharpnessRaw * (0.72 + 0.28 * noiseScore / 100.0));

        // Blur combines edge acutance with the contrast of strong edges. Smooth/low-detail
        // scenes with almost no measurable edges get a neutral score instead of being called blurry.
        var edgeContrast1 = level1.EdgeFraction < 0.006 ? 78.0 : MapExp(level1.StrongGradientMean, 300.0);
        var edgeContrast2 = level2.EdgeFraction < 0.006 ? 78.0 : MapExp(level2.StrongGradientMean, 260.0);
        var focusBase = Clamp100(sharpnessRaw * 0.62 + edgeContrast1 * 0.24 + edgeContrast2 * 0.14);
        var directionality = Math.Max(level1.OrientationPeakShare, level2.OrientationPeakShare);
        var directionPenalty = Clamp01((directionality - 0.38) / 0.32) *
                               Clamp01((82.0 - sharpnessScore) / 82.0) * 15.0;
        var blurScore = Clamp100(focusBase - directionPenalty);

        var totalPixels = (double)pixels.Length;
        var mean = luminanceSum / totalPixels;
        var darkClip = histogram.Take(3).Sum() * 100.0 / totalPixels;
        var brightClip = histogram.Skip(253).Sum() * 100.0 / totalPixels;
        var p01 = Percentile(histogram, totalPixels, 0.01);
        var p05 = Percentile(histogram, totalPixels, 0.05);
        var p50 = Percentile(histogram, totalPixels, 0.50);
        var p95 = Percentile(histogram, totalPixels, 0.95);
        var p99 = Percentile(histogram, totalPixels, 0.99);

        // v2 implicitly wanted every frame's mean luminance near 127.5. v3 instead protects
        // highlights/shadows and only gently penalizes truly extreme histograms, so snow, night,
        // sunsets and silhouettes are no longer treated as "incorrectly exposed" by definition.
        var clippingPenalty =
            Math.Min(38.0, Math.Max(0, darkClip - 0.20) * 2.15) +
            Math.Min(46.0, Math.Max(0, brightClip - 0.12) * 3.15);
        var extremeMedianPenalty = p50 < 14
            ? Math.Min(12.0, (14 - p50) * 0.85)
            : p50 > 241
                ? Math.Min(12.0, (p50 - 241) * 0.85)
                : 0.0;
        var tailPenalty = (p05 <= 1 ? 5.0 : 0.0) + (p95 >= 254 ? 6.0 : 0.0);
        var exposureScore = Clamp100(100.0 - clippingPenalty - extremeMedianPenalty - tailPenalty);

        var tonalSpan = p95 - p05;
        var broadSpan = p99 - p01;
        var tonalScore = tonalSpan switch
        {
            < 18 => Lerp(28, 48, tonalSpan / 18.0),
            < 45 => Lerp(48, 70, (tonalSpan - 18) / 27.0),
            < 90 => Lerp(70, 88, (tonalSpan - 45) / 45.0),
            < 155 => Lerp(88, 100, (tonalSpan - 90) / 65.0),
            _ => 100
        };
        var localContrast = MapExp(level2.GradientMean, 38.0);
        var contrastScore = Clamp100(tonalScore * 0.72 + localContrast * 0.28);

        var megapixels = Math.Max(0, item.Width * (double)Math.Max(0, item.Height) / 1_000_000.0);
        var resolutionScore = ComputePreservationResolutionScore(megapixels);
        var compression = ComputeCompressionMetrics(item.FullPath);

        // Resolution is intentionally NOT part of technicalTotal in v3. It remains a preservation
        // signal/tie-breaker so an old 6 MP original does not lose to a technically worse 24 MP frame.
        var technicalTotal = Clamp100(
            sharpnessScore * 0.30 +
            blurScore * 0.18 +
            exposureScore * 0.16 +
            contrastScore * 0.12 +
            noiseScore * 0.14 +
            compression.Score * 0.10);

        token.ThrowIfCancellationRequested();
        var face = _faceQualityAnalyzer.AnalyzeBgr(faceBgr, token);

        var total = technicalTotal;
        if (face.IsAvailable && face.FaceCount > 0 && face.AggregateFaceScore >= 0)
        {
            // Tiny incidental background faces should hardly change a landscape's score; large faces
            // and group portraits are increasingly driven by face quality. The worst main face is
            // already represented inside AggregateFaceScore.
            var rawGroupBonus = face.ImportantFaceCount >= 2
                ? Math.Min(0.05, (face.ImportantFaceCount - 1) * 0.0125)
                : 0.0;
            // Group count must not resurrect the old "any tiny face changes Quality" problem.
            // Scale the bonus by actual visual importance; incidental/background detections stay near zero.
            var groupBonus = rawGroupBonus * Math.Sqrt(Math.Clamp(face.Importance, 0, 1));
            // v3.0 forced every detected face to affect the total by at least 5%, which let a
            // tiny incidental/false-positive background face perturb an otherwise unrelated shot.
            // v3.1 starts at zero and grows with actual visual importance; a real group still gets
            // a small count bonus so several smaller faces are not ignored.
            var faceWeight = Math.Clamp(face.Importance * 0.38 + groupBonus, 0.0, 0.40);
            if (faceWeight > 0.001)
                total = Clamp100(technicalTotal * (1.0 - faceWeight) + face.AggregateFaceScore * faceWeight);
        }

        // A high-confidence closed-eye signal on a main face is one of the strongest practical
        // reasons to reject a frame from a burst. Keep it separate and explainable. The eye
        // heuristic is deliberately conservative; Unknown never creates a penalty.
        var appliedBlinkPenalty = 0.0;
        if (face.BlinkPenalty > 0 && face.ClosedEyeCount > 0)
        {
            // No fixed 60% floor: a small incidental face must not knock several points off a
            // landscape merely because it happens to be the largest detected face. Square-root
            // scaling keeps blinks meaningful in real group shots while going to zero with importance.
            var blinkInfluence = Math.Sqrt(Math.Clamp(face.Importance, 0, 1));
            appliedBlinkPenalty = face.BlinkPenalty * blinkInfluence;
            total = Clamp100(total - appliedBlinkPenalty);
        }

        var notes = BuildNotes(
            sharpnessScore, blurScore, exposureScore, contrastScore, noiseScore,
            resolutionScore, megapixels, darkClip, brightClip, p05, p95, broadSpan,
            compression, directionality);
        if (!string.IsNullOrWhiteSpace(face.Notes)) notes += "; " + face.Notes;

        return new QualityMetrics
        {
            TotalScore = total,
            TechnicalScore = technicalTotal,
            SharpnessScore = sharpnessScore,
            BlurScore = blurScore,
            ExposureScore = exposureScore,
            ContrastScore = contrastScore,
            NoiseScore = noiseScore,
            ResolutionScore = resolutionScore,
            CompressionScore = compression.Score,
            LaplacianVariance = level1.LaplacianVariance,
            GradientStrength = level1.GradientMean,
            MeanLuminance = mean,
            DarkClipPercent = darkClip,
            BrightClipPercent = brightClip,
            FaceAnalysisAvailable = face.IsAvailable,
            FaceCount = face.FaceCount,
            EyeCount = face.EyeCount,
            FaceScore = face.AggregateFaceScore,
            EyeScore = face.EyeScore,
            EyeOpennessScore = face.EyeOpennessScore,
            ClosedEyeCount = face.ClosedEyeCount,
            BlinkPenalty = appliedBlinkPenalty,
            FacePoseScore = face.PoseScore,
            WorstFaceScore = face.WorstFaceScore,
            Notes = notes
        };
    }

    private static Mat ResizeForTechnicalAnalysis(Mat source, int maxDimension)
    {
        var largest = Math.Max(source.Width, source.Height);
        if (maxDimension <= 0 || largest <= maxDimension)
            return source.Clone();

        var scale = maxDimension / (double)largest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Mat();
        Cv2.Resize(source, resized, new Size(width, height), 0, 0, InterpolationFlags.Area);
        return resized;
    }

    private static DetailStats ComputeDetailStats(byte[] pixels, int width, int height, CancellationToken token)
    {
        double lapSum = 0;
        double lapSqSum = 0;
        double gradientSum = 0;
        double flatResidualSum = 0;
        long flatSamples = 0;
        double strongLapAbsSum = 0;
        double strongGradientSum = 0;
        long strongSamples = 0;
        long samples = 0;
        var orientation = new double[12];
        double orientationWeight = 0;

        for (var y = 1; y < height - 1; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            var row = y * width;
            var previous = row - width;
            var next = row + width;

            for (var x = 1; x < width - 1; x++)
            {
                var center = pixels[row + x];
                var left = pixels[row + x - 1];
                var right = pixels[row + x + 1];
                var up = pixels[previous + x];
                var down = pixels[next + x];

                var lap = 4.0 * center - left - right - up - down;
                lapSum += lap;
                lapSqSum += lap * lap;

                var gx =
                    -pixels[previous + x - 1] - 2.0 * left - pixels[next + x - 1]
                    + pixels[previous + x + 1] + 2.0 * right + pixels[next + x + 1];
                var gy =
                    -pixels[previous + x - 1] - 2.0 * up - pixels[previous + x + 1]
                    + pixels[next + x - 1] + 2.0 * down + pixels[next + x + 1];
                var magnitude = Math.Sqrt(gx * gx + gy * gy);
                gradientSum += magnitude;
                samples++;

                // Flat-area high-frequency residual is a cheap but useful sensor/JPEG noise estimate.
                // Restricting it to low-gradient pixels avoids treating hair/grass/text as "noise".
                if (magnitude < 18.0)
                {
                    var neighborMean = (left + right + up + down) / 4.0;
                    flatResidualSum += Math.Abs(center - neighborMean);
                    flatSamples++;
                }

                if (magnitude >= 40.0)
                {
                    strongLapAbsSum += Math.Abs(lap);
                    strongGradientSum += magnitude;
                    strongSamples++;
                }

                if (magnitude >= 28.0)
                {
                    var angle = Math.Atan2(gy, gx);
                    if (angle < 0) angle += Math.PI;
                    if (angle >= Math.PI) angle -= Math.PI;
                    var bin = Math.Clamp((int)(angle / Math.PI * orientation.Length), 0, orientation.Length - 1);
                    orientation[bin] += magnitude;
                    orientationWeight += magnitude;
                }
            }
        }

        var count = Math.Max(1, samples);
        var lapMean = lapSum / count;
        var lapVariance = Math.Max(0, lapSqSum / count - lapMean * lapMean);
        var gradientMean = gradientSum / count;
        var residual = flatSamples <= 0 ? 0 : flatResidualSum / flatSamples;
        var peak = orientationWeight <= 0 ? 0 : orientation.Max() / orientationWeight;
        var strongGradientMean = strongSamples <= 0 ? 0 : strongGradientSum / strongSamples;
        var edgeAcuity = strongSamples <= 0 || strongGradientSum <= 0
            ? 0
            : (strongLapAbsSum / strongSamples) / strongGradientMean;
        var edgeFraction = strongSamples / (double)count;
        return new DetailStats(lapVariance, gradientMean, residual, peak, edgeAcuity, edgeFraction, strongGradientMean);
    }

    private static byte[] Downsample2x(byte[] source, int width, int height, out int newWidth, out int newHeight)
    {
        newWidth = Math.Max(1, width / 2);
        newHeight = Math.Max(1, height / 2);
        if (width < 2 || height < 2) return source;

        var result = new byte[newWidth * newHeight];
        for (var y = 0; y < newHeight; y++)
        {
            var sy = y * 2;
            var row0 = sy * width;
            var row1 = Math.Min(height - 1, sy + 1) * width;
            var dst = y * newWidth;
            for (var x = 0; x < newWidth; x++)
            {
                var sx = x * 2;
                var sx1 = Math.Min(width - 1, sx + 1);
                var sum = source[row0 + sx] + source[row0 + sx1] + source[row1 + sx] + source[row1 + sx1];
                result[dst + x] = (byte)((sum + 2) / 4);
            }
        }
        return result;
    }

    private static byte[] CopyGrayPixels(Mat gray)
    {
        // gray is the direct destination of Cv2.CvtColor above, so OpenCV allocates it as a
        // contiguous owned Mat. Avoid per-pixel managed calls in this hot path.
        var length = checked(gray.Width * gray.Height);
        var pixels = new byte[length];
        Marshal.Copy(gray.Data, pixels, 0, length);
        return pixels;
    }

    private static double ComputeNoiseScore(double residual)
    {
        if (residual <= 1.4) return 100;
        if (residual < 3.0) return Lerp(100, 91, (residual - 1.4) / 1.6);
        if (residual < 5.0) return Lerp(91, 76, (residual - 3.0) / 2.0);
        if (residual < 8.0) return Lerp(76, 52, (residual - 5.0) / 3.0);
        if (residual < 14.0) return Lerp(52, 20, (residual - 8.0) / 6.0);
        return Math.Max(5, 20 - (residual - 14.0) * 1.2);
    }

    private static double ComputePreservationResolutionScore(double megapixels)
    {
        if (megapixels <= 0) return 0;
        if (megapixels < 0.5) return Lerp(18, 45, megapixels / 0.5);
        if (megapixels < 2.0) return Lerp(45, 66, (megapixels - 0.5) / 1.5);
        if (megapixels < 6.0) return Lerp(66, 83, (megapixels - 2.0) / 4.0);
        if (megapixels < 12.0) return Lerp(83, 93, (megapixels - 6.0) / 6.0);
        if (megapixels < 24.0) return Lerp(93, 98, (megapixels - 12.0) / 12.0);
        return 100;
    }

    private static CompressionMetrics ComputeCompressionMetrics(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return new CompressionMetrics(96.0, -1, "не JPEG");

        try
        {
            var estimatedQuality = TryEstimateJpegQualityFromQuantization(path);
            if (estimatedQuality <= 0)
                return new CompressionMetrics(72.0, -1, "JPEG DQT не прочитан");

            var score = estimatedQuality switch
            {
                < 30 => Lerp(12, 40, estimatedQuality / 30.0),
                < 50 => Lerp(40, 65, (estimatedQuality - 30) / 20.0),
                < 70 => Lerp(65, 82, (estimatedQuality - 50) / 20.0),
                < 85 => Lerp(82, 93, (estimatedQuality - 70) / 15.0),
                _ => Lerp(93, 100, (estimatedQuality - 85) / 15.0)
            };
            return new CompressionMetrics(Clamp100(score), estimatedQuality, "JPEG DQT");
        }
        catch
        {
            return new CompressionMetrics(72.0, -1, "JPEG DQT ошибка");
        }
    }

    private static double TryEstimateJpegQualityFromQuantization(string path)
    {
        const int maxHeaderBytes = 512 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var length = (int)Math.Min(maxHeaderBytes, stream.Length);
        if (length < 8) return -1;
        var data = new byte[length];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = stream.Read(data, offset, data.Length - offset);
            if (read <= 0) break;
            offset += read;
        }
        if (offset < 4 || data[0] != 0xFF || data[1] != 0xD8) return -1;

        int[]? fallbackTable = null;
        var pos = 2;
        while (pos + 3 < offset)
        {
            while (pos < offset && data[pos] != 0xFF) pos++;
            while (pos < offset && data[pos] == 0xFF) pos++;
            if (pos >= offset) break;
            var marker = data[pos++];

            if (marker == 0xD9 || marker == 0xDA) break;
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            if (pos + 1 >= offset) break;

            var segmentLength = (data[pos] << 8) | data[pos + 1];
            if (segmentLength < 2 || pos + segmentLength > offset) break;
            var payload = pos + 2;
            var end = pos + segmentLength;

            if (marker == 0xDB)
            {
                while (payload < end)
                {
                    var info = data[payload++];
                    var precision = info >> 4;
                    var tableId = info & 0x0F;
                    var bytesPerValue = precision == 0 ? 1 : precision == 1 ? 2 : 0;
                    if (bytesPerValue == 0 || payload + 64 * bytesPerValue > end) break;

                    var table = new int[64];
                    for (var i = 0; i < 64; i++)
                    {
                        if (bytesPerValue == 1)
                            table[i] = data[payload++];
                        else
                        {
                            table[i] = (data[payload] << 8) | data[payload + 1];
                            payload += 2;
                        }
                    }

                    if (tableId == 0) return EstimateQualityFromLuminanceTable(table);
                    fallbackTable ??= table;
                }
            }
            pos += segmentLength;
        }

        return fallbackTable is null ? -1 : EstimateQualityFromLuminanceTable(fallbackTable);
    }

    private static double EstimateQualityFromLuminanceTable(int[] zigZagTable)
    {
        if (zigZagTable.Length != 64) return -1;
        var scales = new double[64];
        for (var i = 0; i < 64; i++)
        {
            var standard = StandardLuminanceQuantization[ZigZagOrder[i]];
            if (standard <= 0 || zigZagTable[i] <= 0) return -1;
            scales[i] = zigZagTable[i] * 100.0 / standard;
        }
        Array.Sort(scales);
        var scale = (scales[31] + scales[32]) / 2.0;
        if (scale <= 0) return -1;
        var quality = scale <= 100.0 ? (200.0 - scale) / 2.0 : 5000.0 / scale;
        return Math.Clamp(quality, 1.0, 100.0);
    }

    private static double Percentile(long[] histogram, double totalPixels, double percentile)
    {
        var target = Math.Max(1.0, totalPixels * Math.Clamp(percentile, 0, 1));
        long cumulative = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target) return i;
        }
        return 255;
    }

    private static string BuildNotes(
        double sharpness, double blur, double exposure, double contrast, double noise,
        double resolution, double megapixels, double darkClip, double brightClip,
        double p05, double p95, double broadSpan, CompressionMetrics compression,
        double directionality)
    {
        var parts = new List<string>();
        parts.Add(sharpness >= 82 && blur >= 78
            ? "высокая многомасштабная детализация"
            : sharpness < 48 || blur < 48
                ? "возможен расфокус/смаз"
                : "средняя детализация");

        if (directionality > 0.52 && blur < 70)
            parts.Add("есть направленность границ, совместимая со смазом движения");

        if (exposure >= 86)
            parts.Add("света и тени без явного клиппинга");
        else if (darkClip + brightClip > 5)
            parts.Add($"клиппинг: тени {darkClip:0.0}% / света {brightClip:0.0}%");
        else
            parts.Add("экспозиция пограничная");

        parts.Add(contrast >= 78
            ? $"хороший тональный диапазон ({p05:0}–{p95:0})"
            : $"сдержанный тональный диапазон ({p05:0}–{p95:0}; P99–P01 {broadSpan:0})");

        if (noise < 55) parts.Add("заметный высокочастотный шум");
        else if (noise >= 88) parts.Add("низкий уровень шума");

        if (compression.EstimatedQuality > 0)
            parts.Add($"JPEG ≈ Q{compression.EstimatedQuality:0} по таблицам квантования");
        else if (compression.Score < 80)
            parts.Add("качество JPEG не удалось надёжно определить");

        parts.Add($"{megapixels:0.0} МП; сохранность разрешения {resolution:0}/100 (не входит в общий Quality Score)");
        return string.Join("; ", parts);
    }

    private static double MapExp(double value, double scale)
        => Clamp100(100.0 * (1.0 - Math.Exp(-Math.Max(0, value) / Math.Max(1e-9, scale))));
    private static double Clamp100(double value) => Math.Clamp(value, 0, 100);
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);

    private readonly record struct DetailStats(
        double LaplacianVariance,
        double GradientMean,
        double FlatResidual,
        double OrientationPeakShare,
        double EdgeAcuity,
        double EdgeFraction,
        double StrongGradientMean);

    private readonly record struct CompressionMetrics(
        double Score,
        double EstimatedQuality,
        string Source);
}
