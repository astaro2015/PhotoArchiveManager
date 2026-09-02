using OpenCvSharp;
using OpenCvSharp.Dnn;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class PeopleAnalyzer
{
    public const int AlgorithmVersion = FaceIndexCandidate.PeopleAnalyzerAlgorithmVersion;

    private readonly DatabaseService _database;
    private readonly FaceModelService _models;
    private readonly string _faceThumbnailDirectory;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _internalCts;

    public bool IsPaused => _pauseGate.IsPaused;

    public PeopleAnalyzer(DatabaseService database, FaceModelService models, string faceThumbnailDirectory)
    {
        _database = database;
        _models = models;
        _faceThumbnailDirectory = faceThumbnailDirectory;
    }

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _internalCts?.Cancel();

    public async Task AnalyzeAsync(IProgress<FaceScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_internalCts is not null)
            throw new InvalidOperationException("Индексация лиц уже выполняется.");

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _internalCts.Token;
        _pauseGate.Resume();

        try
        {
            _models.EnsureRecognitionReady();
            Directory.CreateDirectory(_faceThumbnailDirectory);
            var candidates = await _database.GetFaceIndexCandidatesAsync(token);
            var total = candidates.Count;
            var processed = 0;
            var computed = 0;
            var cached = 0;
            var errors = 0;
            var facesFound = 0;

            progress?.Report(new FaceScanProgress { Stage = "Подготовка индекса лиц", TotalFiles = total });

            // OpenCvSharp4 4.13 exposes ONNX loading on Net itself.  Do not use
            // CvDnn.ReadNetFromONNX here: that facade differs between OpenCvSharp
            // package generations.  YuNet is created per image below so we also do
            // not depend on FaceDetectorYN.SetInputSize, which is absent from some
            // 4.13 managed builds even though native OpenCV provides it.
            using var recognizer = Net.ReadNetFromONNX(_models.SFaceModelPath);
            if (recognizer is null)
                throw new InvalidOperationException("Не удалось открыть локальную SFace ONNX-модель.");
            recognizer.SetPreferableBackend(Backend.OPENCV);
            recognizer.SetPreferableTarget(Target.CPU);

            foreach (var item in candidates)
            {
                token.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(token);

                try
                {
                    if (item.HasCurrentFaceIndex)
                    {
                        cached++;
                        processed++;
                        if (!string.IsNullOrWhiteSpace(item.FaceIndexError)) errors++;
                        facesFound += item.CachedFaceCount;
                        Report(progress, "Индекс лиц", item.FullPath, total, processed, computed, cached, errors, facesFound);
                        continue;
                    }

                    if (!File.Exists(item.FullPath))
                        throw new FileNotFoundException("Файл отсутствует. Выполните повторное сканирование.", item.FullPath);
                    var info = new FileInfo(item.FullPath);
                    if (info.Length != item.FileSize || info.LastWriteTimeUtc.Ticks != item.LastWriteUtcTicks)
                        throw new IOException("Файл изменился после индексации. Выполните повторное сканирование библиотеки.");

                    progress?.Report(new FaceScanProgress
                    {
                        Stage = "Поиск и описание лиц",
                        CurrentFile = item.FullPath,
                        TotalFiles = total,
                        ProcessedFiles = processed,
                        ComputedFiles = computed,
                        CachedFiles = cached,
                        ErrorFiles = errors,
                        FacesFound = facesFound
                    });

                    var oldThumbs = await _database.GetFaceThumbnailPathsForFileAsync(item.Id, token);
                    var drafts = await Task.Run(() => AnalyzeOne(item, recognizer, token), token);
                    await _database.ReplaceDetectedFacesAsync(item.Id, item.FileSize, item.LastWriteUtcTicks, AlgorithmVersion, drafts, token);
                    foreach (var old in oldThumbs)
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(old) && File.Exists(old) && !drafts.Any(x => string.Equals(x.ThumbnailPath, old, StringComparison.OrdinalIgnoreCase)))
                                File.Delete(old);
                        }
                        catch { }
                    }
                    computed++;
                    facesFound += drafts.Count();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    LoggingService.Error("Face indexing failed: " + item.FullPath, ex);
                    await _database.MarkFaceIndexErrorAsync(item.Id, item.FileSize, item.LastWriteUtcTicks, AlgorithmVersion, ex.Message, token);
                }

                processed++;
                if (processed % 10 == 0 || processed == total)
                    Report(progress, "Индекс лиц", item.FullPath, total, processed, computed, cached, errors, facesFound);
            }

            progress?.Report(new FaceScanProgress
            {
                Stage = "Готово",
                TotalFiles = total,
                ProcessedFiles = processed,
                ComputedFiles = computed,
                CachedFiles = cached,
                ErrorFiles = errors,
                FacesFound = facesFound
            });
        }
        finally
        {
            _internalCts?.Dispose();
            _internalCts = null;
        }
    }

    public async Task<PeopleGroupingResult> GroupUnknownFacesAsync(double threshold, int minimumGroupSize, CancellationToken cancellationToken = default)
    {
        threshold = Math.Clamp(threshold, 0.35, 0.80);
        minimumGroupSize = Math.Clamp(minimumGroupSize, 2, 20);

        // Preserve every group the user has already named. Only unnamed automatic suggestions are rebuilt.
        await _database.ResetUnnamedAutomaticPeopleAsync(cancellationToken);
        var faces = await _database.GetUngroupedFaceEmbeddingsAsync(cancellationToken);
        var usable = faces
            .Where(x => x.Embedding.Length >= 64 && x.QualityScore >= 18)
            .OrderByDescending(x => x.QualityScore)
            .ThenBy(x => x.FaceId)
            .ToList();

        var clusters = await Task.Run(() => BuildClusters(usable, threshold, cancellationToken), cancellationToken);
        var keep = clusters.Where(x => x.Members.Count >= minimumGroupSize).OrderByDescending(x => x.Members.Count).ToList();

        var grouped = 0;
        foreach (var cluster in keep)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _database.CreateAutomaticPersonGroupAsync(cluster.Members.Select(x => x.FaceId).ToArray(), cancellationToken);
            grouped += cluster.Members.Count;
        }

        return new PeopleGroupingResult(usable.Count, keep.Count, grouped, Math.Max(0, usable.Count - grouped));
    }

    private List<DetectedFaceDraft> AnalyzeOne(
        FaceIndexCandidate item,
        Net recognizer,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var image = ImageMatLoader.LoadBgr(item.FullPath, item.Orientation, 2400);

        // Create YuNet with the actual input dimensions.  This deliberately avoids
        // SetInputSize(), which is missing from the OpenCvSharp4 4.13 managed API
        // shipped by OpenCvSharp4.Windows 4.13.0.20260627 on the user's SDK.
        using var detector = FaceDetectorYN.Create(
            _models.YuNetModelPath, "", image.Size(),
            scoreThreshold: 0.78f, nmsThreshold: 0.30f, topK: 5000,
            backendId: Backend.OPENCV, targetId: Target.CPU);
        using var detections = new Mat();
        detector.Detect(image, detections);

        if (detections.Empty() || detections.Rows <= 0)
            return new List<DetectedFaceDraft>();

        var rows = Math.Min(detections.Rows, 32);
        var result = new List<DetectedFaceDraft>(rows);
        for (var i = 0; i < rows; i++)
        {
            token.ThrowIfCancellationRequested();
            var x = (int)Math.Floor(detections.At<float>(i, 0));
            var y = (int)Math.Floor(detections.At<float>(i, 1));
            var w = (int)Math.Ceiling(detections.At<float>(i, 2));
            var h = (int)Math.Ceiling(detections.At<float>(i, 3));
            var score = detections.Cols > 14 ? detections.At<float>(i, 14) : 1f;
            if (score < 0.78f || w < 24 || h < 24) continue;

            var rawRect = ClipRect(new Rect(x, y, w, h), image.Width, image.Height);
            if (rawRect.Width < 24 || rawRect.Height < 24) continue;
            var expanded = ExpandToSquare(rawRect, image.Width, image.Height, 0.18);

            // OpenCvSharp4 4.13 does not expose FaceRecognizerSF in its managed API.
            // Reproduce OpenCV's SFace path explicitly: YuNet 5 landmarks -> similarity
            // transform to the canonical 112x112 face -> SFace ONNX DNN inference.
            using var aligned = AlignFaceForSFace(image, detections, i);
            if (aligned.Empty()) continue;

            var embedding = ExtractEmbedding(aligned, recognizer);
            if (embedding.Length < 64) continue;

            using var crop = new Mat(image, expanded).Clone();
            var quality = ComputeFaceQuality(crop, rawRect, image.Width, image.Height);
            var thumbPath = Path.Combine(_faceThumbnailDirectory, $"f{item.Id}_{Guid.NewGuid():N}.jpg");
            using (var thumb = ResizeForThumbnail(crop, 220))
                Cv2.ImWrite(thumbPath, thumb);

            result.Add(new DetectedFaceDraft
            {
                X = rawRect.X,
                Y = rawRect.Y,
                Width = rawRect.Width,
                Height = rawRect.Height,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                QualityScore = quality,
                Embedding = embedding,
                ThumbnailPath = thumbPath
            });
        }
        return result;
    }

    private static Mat AlignFaceForSFace(Mat source, Mat detections, int row)
    {
        // YuNet row layout: x, y, w, h, then 5 landmark pairs, then score.
        // SFace's canonical target landmarks are the same ones used by OpenCV's
        // FaceRecognizerSF::alignCrop implementation. We solve the least-squares
        // 2-D similarity transform x' = a*x - b*y + tx; y' = b*x + a*y + ty.
        Span<double> sx = stackalloc double[5];
        Span<double> sy = stackalloc double[5];
        for (var i = 0; i < 5; i++)
        {
            sx[i] = detections.At<float>(row, 4 + i * 2);
            sy[i] = detections.At<float>(row, 5 + i * 2);
        }

        ReadOnlySpan<double> dx = stackalloc double[5] { 38.2946, 73.5318, 56.0252, 41.5493, 70.7299 };
        ReadOnlySpan<double> dy = stackalloc double[5] { 51.6963, 51.5014, 71.7366, 92.3655, 92.2041 };

        double smx = 0, smy = 0, dmx = 0, dmy = 0;
        for (var i = 0; i < 5; i++)
        {
            smx += sx[i];
            smy += sy[i];
            dmx += dx[i];
            dmy += dy[i];
        }
        smx /= 5.0; smy /= 5.0; dmx /= 5.0; dmy /= 5.0;

        double denom = 0, anum = 0, bnum = 0;
        for (var i = 0; i < 5; i++)
        {
            var x = sx[i] - smx;
            var y = sy[i] - smy;
            var u = dx[i] - dmx;
            var v = dy[i] - dmy;
            denom += x * x + y * y;
            anum += x * u + y * v;
            bnum += x * v - y * u;
        }

        if (denom < 1e-9) return new Mat();
        var a = anum / denom;
        var b = bnum / denom;
        var tx = dmx - a * smx + b * smy;
        var ty = dmy - b * smx - a * smy;

        using var transform = new Mat(2, 3, MatType.CV_64FC1);
        transform.Set<double>(0, 0, a);
        transform.Set<double>(0, 1, -b);
        transform.Set<double>(0, 2, tx);
        transform.Set<double>(1, 0, b);
        transform.Set<double>(1, 1, a);
        transform.Set<double>(1, 2, ty);

        var aligned = new Mat();
        Cv2.WarpAffine(source, aligned, transform, new Size(112, 112), InterpolationFlags.Linear, BorderTypes.Constant);
        return aligned;
    }

    private static float[] ExtractEmbedding(Mat alignedBgr, Net recognizer)
    {
        // Mirrors OpenCV FaceRecognizerSF::feature(): scale=1, 112x112, zero mean,
        // swap B/R, no crop; then a normal forward pass through SFace.
        using var blob = CvDnn.BlobFromImage(
            alignedBgr, 1.0, new Size(112, 112), new Scalar(0, 0, 0), true, false);
        recognizer.SetInput(blob);
        using var feature = recognizer.Forward();
        if (feature.Empty()) return Array.Empty<float>();
        using var flat = feature.Reshape(1, 1);
        var count = checked((int)flat.Total());
        if (count < 64 || count > 4096 || flat.Type() != MatType.CV_32FC1)
            return Array.Empty<float>();

        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = flat.At<float>(0, i);
        Normalize(values);
        return values;
    }

    private static Rect ClipRect(Rect source, int width, int height)
    {
        var x1 = Math.Clamp(source.X, 0, Math.Max(0, width - 1));
        var y1 = Math.Clamp(source.Y, 0, Math.Max(0, height - 1));
        var x2 = Math.Clamp(source.X + source.Width, x1 + 1, width);
        var y2 = Math.Clamp(source.Y + source.Height, y1 + 1, height);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    private static double ComputeFaceQuality(Mat crop, Rect rect, int imageWidth, int imageHeight)
    {
        using var gray = new Mat();
        using var lap = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var stddev);
        var variance = stddev.Val0 * stddev.Val0;
        var sharpness = 100.0 * (1.0 - Math.Exp(-variance / 320.0));
        var areaRatio = rect.Width * (double)rect.Height / Math.Max(1.0, imageWidth * (double)imageHeight);
        var sizeScore = Math.Clamp(100.0 * Math.Sqrt(areaRatio / 0.04), 0, 100);
        return Math.Clamp(sharpness * 0.72 + sizeScore * 0.28, 0, 100);
    }

    private static Mat ResizeForThumbnail(Mat source, int maxSide)
    {
        var largest = Math.Max(source.Width, source.Height);
        if (largest <= maxSide) return source.Clone();
        var scale = maxSide / (double)largest;
        var result = new Mat();
        Cv2.Resize(source, result, new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale))), 0, 0, InterpolationFlags.Area);
        return result;
    }

    private static Rect ExpandToSquare(Rect source, int width, int height, double margin)
    {
        var cx = source.X + source.Width / 2.0;
        var cy = source.Y + source.Height / 2.0;
        var side = Math.Max(source.Width, source.Height) * (1.0 + 2.0 * margin);
        var x = (int)Math.Floor(cx - side / 2.0);
        var y = (int)Math.Floor(cy - side / 2.0);
        var s = Math.Max(1, (int)Math.Ceiling(side));
        x = Math.Clamp(x, 0, Math.Max(0, width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, height - 1));
        s = Math.Min(s, Math.Min(width - x, height - y));
        return new Rect(x, y, Math.Max(1, s), Math.Max(1, s));
    }

    private static List<FaceCluster> BuildClusters(IReadOnlyList<FaceEmbeddingCandidate> faces, double threshold, CancellationToken token)
    {
        var clusters = new List<FaceCluster>();
        foreach (var face in faces)
        {
            token.ThrowIfCancellationRequested();
            FaceCluster? best = null;
            var bestScore = double.NegativeInfinity;

            foreach (var cluster in clusters)
            {
                // The same person should not normally appear twice in one ordinary photo. This simple
                // constraint prevents many catastrophic false merges in group shots.
                if (cluster.FileIds.Contains(face.FileId)) continue;
                var score = Cosine(face.Embedding, cluster.Centroid);
                if (score <= bestScore) continue;
                bestScore = score;
                best = cluster;
            }

            if (best is not null && bestScore >= threshold)
                best.Add(face);
            else
                clusters.Add(new FaceCluster(face));
        }

        // Conservative merge pass: only merge already coherent clusters with a little extra margin.
        var mergeThreshold = Math.Min(0.85, threshold + 0.035);
        for (var i = 0; i < clusters.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (clusters[i].Members.Count == 0) continue;
            for (var j = i + 1; j < clusters.Count; j++)
            {
                if (clusters[j].Members.Count == 0) continue;
                if (clusters[i].FileIds.Overlaps(clusters[j].FileIds)) continue;
                if (Cosine(clusters[i].Centroid, clusters[j].Centroid) < mergeThreshold) continue;
                clusters[i].AddRange(clusters[j].Members);
                clusters[j].Clear();
            }
        }
        return clusters.Where(x => x.Members.Count > 0).ToList();
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return -1;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot; // both vectors/centroids are normalized
    }

    private static void Normalize(float[] values)
    {
        double sum = 0;
        for (var i = 0; i < values.Length; i++) sum += values[i] * values[i];
        var norm = Math.Sqrt(sum);
        if (norm < 1e-12) return;
        for (var i = 0; i < values.Length; i++) values[i] = (float)(values[i] / norm);
    }

    private static void Report(IProgress<FaceScanProgress>? progress, string stage, string file, int total, int processed, int computed, int cached, int errors, int faces)
    {
        progress?.Report(new FaceScanProgress
        {
            Stage = stage,
            CurrentFile = file,
            TotalFiles = total,
            ProcessedFiles = processed,
            ComputedFiles = computed,
            CachedFiles = cached,
            ErrorFiles = errors,
            FacesFound = faces
        });
    }

    private sealed class FaceCluster
    {
        private double[] _sum;
        public List<FaceEmbeddingCandidate> Members { get; } = new();
        public HashSet<long> FileIds { get; } = new();
        public float[] Centroid { get; private set; }

        public FaceCluster(FaceEmbeddingCandidate first)
        {
            _sum = first.Embedding.Select(x => (double)x).ToArray();
            Centroid = first.Embedding.ToArray();
            Members.Add(first);
            FileIds.Add(first.FileId);
        }

        public void Add(FaceEmbeddingCandidate face)
        {
            Members.Add(face);
            FileIds.Add(face.FileId);
            if (_sum.Length != face.Embedding.Length) return;
            for (var i = 0; i < _sum.Length; i++) _sum[i] += face.Embedding[i];
            RebuildCentroid();
        }

        public void AddRange(IEnumerable<FaceEmbeddingCandidate> faces)
        {
            foreach (var face in faces) Add(face);
        }

        public void Clear()
        {
            Members.Clear();
            FileIds.Clear();
            _sum = Array.Empty<double>();
            Centroid = Array.Empty<float>();
        }

        private void RebuildCentroid()
        {
            var c = new float[_sum.Length];
            double norm2 = 0;
            for (var i = 0; i < _sum.Length; i++) norm2 += _sum[i] * _sum[i];
            var norm = Math.Sqrt(norm2);
            if (norm < 1e-12) return;
            for (var i = 0; i < _sum.Length; i++) c[i] = (float)(_sum[i] / norm);
            Centroid = c;
        }
    }
}
