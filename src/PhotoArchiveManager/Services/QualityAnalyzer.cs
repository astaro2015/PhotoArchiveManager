using Microsoft.Data.Sqlite;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoArchiveManager.Services;

public sealed class QualityAnalyzer
{
    private readonly DatabaseService _database;
    private readonly FaceQualityAnalyzer _faceQualityAnalyzer;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _internalCts;

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

                progress?.Report(new QualityProgress
                {
                    Stage = item.HasQuality ? "Оценка качества — из кэша" : "Оценка качества",
                    CurrentFile = item.FullPath,
                    TotalFiles = files.Count,
                    ProcessedFiles = processed,
                    ComputedFiles = computed,
                    CachedFiles = cached,
                    ErrorFiles = errors
                });

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
                    await _database.UpdateQualityAsync(connection, item.Id, item.FileSize, item.LastWriteUtcTicks, metrics, "", token);
                    computed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    LoggingService.Error("Quality analysis failed: " + item.FullPath, ex);
                    await _database.UpdateQualityAsync(connection, item.Id, item.FileSize, item.LastWriteUtcTicks, null, ex.Message, token);
                }

                processed++;
            }

            progress?.Report(new QualityProgress
            {
                Stage = "Оценка качества завершена",
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
        using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = 900;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        BitmapSource source = bitmap;
        if (item.Orientation is 3 or 6 or 8)
        {
            var angle = item.Orientation == 3 ? 180 : item.Orientation == 6 ? 90 : 270;
            var rotated = new TransformedBitmap(source, new RotateTransform(angle));
            rotated.Freeze();
            source = rotated;
        }

        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        gray.Freeze();
        var width = gray.PixelWidth;
        var height = gray.PixelHeight;
        if (width < 5 || height < 5)
            throw new InvalidDataException("Изображение слишком маленькое для оценки качества.");

        var pixels = new byte[width * height];
        gray.CopyPixels(pixels, width, 0);

        var histogram = new long[256];
        long luminanceSum = 0;
        foreach (var p in pixels)
        {
            histogram[p]++;
            luminanceSum += p;
        }

        token.ThrowIfCancellationRequested();

        double lapSum = 0;
        double lapSqSum = 0;
        double gradientSum = 0;
        long samples = 0;

        // A maximum of about 900 px on the long edge keeps the metric fast while retaining
        // enough fine detail for comparing near-duplicate family photographs.
        for (var y = 1; y < height - 1; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            var row = y * width;
            var previous = row - width;
            var next = row + width;

            for (var x = 1; x < width - 1; x++)
            {
                var center = pixels[row + x];
                var lap = 4.0 * center - pixels[row + x - 1] - pixels[row + x + 1] - pixels[previous + x] - pixels[next + x];
                lapSum += lap;
                lapSqSum += lap * lap;

                var gx =
                    -pixels[previous + x - 1] - 2.0 * pixels[row + x - 1] - pixels[next + x - 1]
                    + pixels[previous + x + 1] + 2.0 * pixels[row + x + 1] + pixels[next + x + 1];
                var gy =
                    -pixels[previous + x - 1] - 2.0 * pixels[previous + x] - pixels[previous + x + 1]
                    + pixels[next + x - 1] + 2.0 * pixels[next + x] + pixels[next + x + 1];
                gradientSum += Math.Sqrt(gx * gx + gy * gy);
                samples++;
            }
        }

        var lapMean = lapSum / Math.Max(1, samples);
        var lapVariance = Math.Max(0, lapSqSum / Math.Max(1, samples) - lapMean * lapMean);
        var gradientStrength = gradientSum / Math.Max(1, samples);

        // Smooth, monotonic mappings. Exact numbers are deliberately called heuristics in the UI.
        var sharpnessScore = Clamp100(100.0 * (1.0 - Math.Exp(-lapVariance / 500.0)));
        var blurScore = Clamp100(100.0 * (1.0 - Math.Exp(-gradientStrength / 55.0)));

        var totalPixels = (double)pixels.Length;
        var mean = luminanceSum / totalPixels;
        var darkClip = histogram.Take(9).Sum() * 100.0 / totalPixels;
        var brightClip = histogram.Skip(247).Sum() * 100.0 / totalPixels;
        var meanPenalty = Math.Abs(mean - 127.5) / 127.5 * 22.0;
        var clippingPenalty = Math.Min(55.0, (darkClip + brightClip) * 1.35);
        var exposureScore = Clamp100(100.0 - meanPenalty - clippingPenalty);

        var megapixels = Math.Max(0, item.Width * (double)Math.Max(0, item.Height) / 1_000_000.0);
        var resolutionScore = Clamp100(100.0 * (1.0 - Math.Exp(-megapixels / 5.0)));
        var compressionScore = ComputeCompressionScore(item);

        var technicalTotal = Clamp100(
            sharpnessScore * 0.34 +
            blurScore * 0.22 +
            exposureScore * 0.16 +
            resolutionScore * 0.20 +
            compressionScore * 0.08);

        token.ThrowIfCancellationRequested();
        var face = _faceQualityAnalyzer.AnalyzeGray(pixels, width, height, token);

        // Portrait-aware weighting is deliberately modest. If no face is detected (or the
        // detector is unavailable), the original technical score is kept unchanged.
        // This prevents landscapes, scans, documents and old family photos without a
        // frontal face from being punished merely because they contain no detectable eyes.
        var total = technicalTotal;
        var faceScore = -1.0;
        if (face.IsAvailable && face.FaceCount > 0 && face.FaceSharpnessScore >= 0)
        {
            faceScore = face.EyeScore >= 0
                ? Clamp100(face.FaceSharpnessScore * 0.72 + face.EyeScore * 0.28)
                : face.FaceSharpnessScore;
            total = Clamp100(technicalTotal * 0.82 + face.FaceSharpnessScore * 0.13 + Math.Max(0, face.EyeScore) * 0.05);
        }

        var notes = BuildNotes(sharpnessScore, blurScore, exposureScore, resolutionScore, compressionScore, megapixels, darkClip, brightClip);
        if (!string.IsNullOrWhiteSpace(face.Notes)) notes += "; " + face.Notes;

        return new QualityMetrics
        {
            TotalScore = total,
            SharpnessScore = sharpnessScore,
            BlurScore = blurScore,
            ExposureScore = exposureScore,
            ResolutionScore = resolutionScore,
            CompressionScore = compressionScore,
            LaplacianVariance = lapVariance,
            GradientStrength = gradientStrength,
            MeanLuminance = mean,
            DarkClipPercent = darkClip,
            BrightClipPercent = brightClip,
            FaceAnalysisAvailable = face.IsAvailable,
            FaceCount = face.FaceCount,
            EyeCount = face.EyeCount,
            FaceScore = faceScore,
            EyeScore = face.EyeScore,
            Notes = notes
        };
    }

    private static double ComputeCompressionScore(VisualDuplicateFileItem item)
    {
        var extension = Path.GetExtension(item.FullPath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return 85.0; // neutral: the JPEG-specific heuristic is not applicable.

        var pixels = item.Width * (double)Math.Max(1, item.Height);
        if (pixels <= 0) return 50;
        var bytesPerPixel = item.FileSize / pixels;

        return bytesPerPixel switch
        {
            < 0.04 => 10,
            < 0.08 => Lerp(10, 30, (bytesPerPixel - 0.04) / 0.04),
            < 0.12 => Lerp(30, 50, (bytesPerPixel - 0.08) / 0.04),
            < 0.18 => Lerp(50, 68, (bytesPerPixel - 0.12) / 0.06),
            < 0.28 => Lerp(68, 84, (bytesPerPixel - 0.18) / 0.10),
            < 0.45 => Lerp(84, 96, (bytesPerPixel - 0.28) / 0.17),
            _ => 100
        };
    }

    private static string BuildNotes(
        double sharpness, double blur, double exposure, double resolution, double compression,
        double megapixels, double darkClip, double brightClip)
    {
        var parts = new List<string>();
        parts.Add(sharpness >= 80 && blur >= 75 ? "высокая детализация" : sharpness < 45 || blur < 45 ? "возможен смаз или мягкий кадр" : "средняя детализация");
        parts.Add(exposure >= 80 ? "экспозиция без явных проблем" : darkClip + brightClip > 12 ? "много проваленных/пересвеченных пикселей" : "экспозиция отклонена от нейтральной");
        parts.Add($"{megapixels:0.0} МП");
        if (compression < 45) parts.Add("возможна сильная JPEG-компрессия");
        else if (compression >= 80) parts.Add("нет явного признака сильного JPEG-пережатия");
        return string.Join("; ", parts);
    }

    private static double Clamp100(double value) => Math.Clamp(value, 0, 100);
    private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);
}
