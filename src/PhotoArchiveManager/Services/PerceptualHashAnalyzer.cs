using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class PerceptualHashAnalyzer
{
    private readonly DatabaseService _database;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _internalCts;

    public bool IsPaused => _pauseGate.IsPaused;

    public PerceptualHashAnalyzer(DatabaseService database) => _database = database;

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _internalCts?.Cancel();

    public async Task<IReadOnlyList<VisualDuplicateGroupItem>> AnalyzeAsync(
        int threshold,
        IProgress<VisualDuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        threshold = Math.Clamp(threshold, 1, 7);
        if (_internalCts is not null)
            throw new InvalidOperationException("Поиск визуальных дублей уже выполняется.");
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _internalCts.Token;
        _pauseGate.Resume();

        try
        {
            var candidates = await _database.GetPerceptualHashCandidatesAsync(token);
            var total = candidates.Count;
            var processed = 0;
            var computed = 0;
            var cached = 0;
            var errors = 0;

            progress?.Report(new VisualDuplicateProgress { Stage = "Подготовка визуальных отпечатков", TotalFiles = total });

            await using var connection = _database.CreateConnection();
            await connection.OpenAsync(token);

            foreach (var item in candidates)
            {
                token.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(token);

                try
                {
                    if (!File.Exists(item.FullPath))
                        throw new FileNotFoundException("Файл отсутствует. Выполните повторное сканирование библиотеки.", item.FullPath);
                    var info = new FileInfo(item.FullPath);
                    if (info.Length != item.FileSize || info.LastWriteTimeUtc.Ticks != item.LastWriteUtcTicks)
                        throw new IOException("Файл изменился после индексации. Выполните повторное сканирование библиотеки.");

                    if (item.HasCurrentPerceptualRecord)
                    {
                        cached++;
                        if (!item.HasValidCachedHash) errors++;
                    }
                    else
                    {
                        progress?.Report(new VisualDuplicateProgress
                        {
                            Stage = "Расчёт dHash/aHash",
                            CurrentFile = item.FullPath,
                            TotalFiles = total,
                            ProcessedFiles = processed,
                            ComputedFiles = computed,
                            CachedFiles = cached,
                            ErrorFiles = errors
                        });

                        var hashes = await Task.Run(() => ComputeHashes(item.FullPath, item.Orientation), token);
                        var after = new FileInfo(item.FullPath);
                        if (!after.Exists || after.Length != item.FileSize || after.LastWriteTimeUtc.Ticks != item.LastWriteUtcTicks)
                            throw new IOException("Файл изменился во время расчёта визуального отпечатка. Выполните повторное сканирование библиотеки.");
                        await _database.UpdatePerceptualHashAsync(
                            connection,
                            item.Id,
                            hashes.DHash,
                            hashes.AHash,
                            item.FileSize,
                            item.LastWriteUtcTicks,
                            "",
                            token);
                        computed++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    LoggingService.Error("Perceptual hash failed: " + item.FullPath, ex);
                    await _database.UpdatePerceptualHashAsync(
                        connection, item.Id, "", "", item.FileSize, item.LastWriteUtcTicks, ex.Message, token);
                }

                processed++;
                if (processed % 25 == 0 || processed == total)
                {
                    progress?.Report(new VisualDuplicateProgress
                    {
                        Stage = "Визуальные отпечатки",
                        CurrentFile = item.FullPath,
                        TotalFiles = total,
                        ProcessedFiles = processed,
                        ComputedFiles = computed,
                        CachedFiles = cached,
                        ErrorFiles = errors
                    });
                }
            }

            progress?.Report(new VisualDuplicateProgress
            {
                Stage = "Группировка похожих изображений",
                TotalFiles = total,
                ProcessedFiles = processed,
                ComputedFiles = computed,
                CachedFiles = cached,
                ErrorFiles = errors
            });

            var refreshed = await _database.GetPerceptualHashCandidatesAsync(token);
            var groups = await Task.Run(() => BuildGroups(refreshed, threshold, token), token);

            progress?.Report(new VisualDuplicateProgress
            {
                Stage = "Готово",
                TotalFiles = total,
                ProcessedFiles = processed,
                ComputedFiles = computed,
                CachedFiles = cached,
                ErrorFiles = errors,
                GroupsFound = groups.Count
            });
            return groups;
        }
        finally
        {
            _internalCts?.Dispose();
            _internalCts = null;
        }
    }

    public async Task<IReadOnlyList<VisualDuplicateGroupItem>> LoadCachedGroupsAsync(
        int threshold,
        CancellationToken cancellationToken = default)
    {
        threshold = Math.Clamp(threshold, 1, 7);
        var candidates = await _database.GetPerceptualHashCandidatesAsync(cancellationToken);
        return await Task.Run(() => BuildGroups(candidates, threshold, cancellationToken), cancellationToken);
    }

    internal static (string DHash, string AHash) ComputeHashes(string path, int orientation)
    {
        // dHash/aHash only need a tiny working image. Decoding a 50 MP JPEG in full merely to
        // reduce it to 9x8 wastes memory and CPU, so v2 asks WIC for a bounded 256 px decode.
        // ImageMatLoader also normalizes all eight EXIF Orientation values.
        BitmapSource source = ImageMatLoader.LoadBitmapSource(path, orientation, 256);

        var scaled = new TransformedBitmap(source,
            new ScaleTransform(9.0 / source.PixelWidth, 8.0 / source.PixelHeight));
        scaled.Freeze();
        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        gray.Freeze();

        var pixels = new byte[9 * 8];
        gray.CopyPixels(pixels, 9, 0);

        ulong dHash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (pixels[y * 9 + x] > pixels[y * 9 + x + 1]) dHash |= 1UL << bit;
                bit++;
            }
        }

        long sum = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                sum += pixels[y * 9 + x];
        var average = sum / 64.0;

        ulong aHash = 0;
        bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (pixels[y * 9 + x] >= average) aHash |= 1UL << bit;
                bit++;
            }
        }

        return (dHash.ToString("x16"), aHash.ToString("x16"));
    }

    private static List<VisualDuplicateGroupItem> BuildGroups(
        IReadOnlyList<PerceptualHashCandidate> source,
        int threshold,
        CancellationToken token)
    {
        var items = source
            .Where(x => x.HasValidCachedHash && TryParseHash(x.DHash, out _) && TryParseHash(x.AHash, out _))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Sha256) ? "id:" + x.Id : "sha:" + x.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.FileSize).ThenBy(x => x.Id).First())
            .OrderBy(x => x.Id)
            .ToList();
        if (items.Count < 2) return new List<VisualDuplicateGroupItem>();

        var dHashes = items.Select(x => ulong.Parse(x.DHash, System.Globalization.NumberStyles.HexNumber)).ToArray();
        var aHashes = items.Select(x => ulong.Parse(x.AHash, System.Globalization.NumberStyles.HexNumber)).ToArray();
        var parents = Enumerable.Range(0, items.Count).ToArray();
        var ranks = new byte[items.Count];
        var buckets = new Dictionary<long, List<int>>();

        for (var i = 0; i < items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = items[i];
            var ratio = AspectRatio(item);
            var aspectBucket = ratio > 0 ? (int)Math.Round(ratio * 50.0) : 0;
            var candidates = new HashSet<int>();

            for (var band = 0; band < 8; band++)
            {
                var value = (byte)(dHashes[i] >> (band * 8));
                for (var ab = aspectBucket - 1; ab <= aspectBucket + 1; ab++)
                {
                    var key = MakeBucketKey(ab, band, value);
                    if (!buckets.TryGetValue(key, out var list)) continue;
                    foreach (var j in list) candidates.Add(j);
                }
            }

            foreach (var j in candidates)
            {
                if (AreKnownExactDuplicates(item, items[j])) continue;
                if (!AspectCompatible(item, items[j])) continue;

                var dDistance = BitOperations.PopCount(dHashes[i] ^ dHashes[j]);
                if (dDistance > threshold) continue;
                var aDistance = BitOperations.PopCount(aHashes[i] ^ aHashes[j]);
                if (aDistance > threshold + 3) continue;
                Union(parents, ranks, i, j);
            }

            for (var band = 0; band < 8; band++)
            {
                var value = (byte)(dHashes[i] >> (band * 8));
                var key = MakeBucketKey(aspectBucket, band, value);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                list.Add(i);
            }
        }

        var grouped = new Dictionary<int, List<int>>();
        for (var i = 0; i < items.Count; i++)
        {
            var root = Find(parents, i);
            if (!grouped.TryGetValue(root, out var list))
            {
                list = new List<int>();
                grouped[root] = list;
            }
            list.Add(i);
        }

        var result = new List<VisualDuplicateGroupItem>();
        foreach (var indices in grouped.Values.Where(x => x.Count > 1))
        {
            token.ThrowIfCancellationRequested();
            // A higher-resolution/larger file is a useful representative for visual comparison,
            // but it is NOT yet called "best". Quality scoring comes in v0.4/v0.5.
            var representativeIndex = indices
                .OrderByDescending(x => (long)items[x].Width * Math.Max(1, items[x].Height))
                .ThenByDescending(x => items[x].FileSize)
                .First();

            var repD = dHashes[representativeIndex];
            var repA = aHashes[representativeIndex];
            var visualFiles = indices
                .Select(x => new VisualDuplicateFileItem
                {
                    Id = items[x].Id,
                    FullPath = items[x].FullPath,
                    FileName = items[x].FileName,
                    SourceFolder = items[x].SourceFolder,
                    ThumbnailPath = items[x].ThumbnailPath,
                    FileSize = items[x].FileSize,
                    LastWriteUtcTicks = items[x].LastWriteUtcTicks,
                    Width = items[x].Width,
                    Height = items[x].Height,
                    Orientation = items[x].Orientation,
                    CaptureDate = items[x].CaptureDate,
                    CameraMake = items[x].CameraMake,
                    CameraModel = items[x].CameraModel,
                    Sha256 = items[x].Sha256,
                    DHash = items[x].DHash,
                    AHash = items[x].AHash,
                    DistanceFromRepresentative = BitOperations.PopCount(dHashes[x] ^ repD),
                    AverageHashDistanceFromRepresentative = BitOperations.PopCount(aHashes[x] ^ repA),
                    HasQuality = items[x].HasValidCachedQuality,
                    QualityScore = items[x].HasValidCachedQuality ? items[x].QualityScore : -1,
                    TechnicalScore = items[x].HasValidCachedQuality ? items[x].TechnicalScore : -1,
                    SharpnessScore = items[x].HasValidCachedQuality ? items[x].SharpnessScore : -1,
                    BlurScore = items[x].HasValidCachedQuality ? items[x].BlurScore : -1,
                    ExposureScore = items[x].HasValidCachedQuality ? items[x].ExposureScore : -1,
                    ContrastScore = items[x].HasValidCachedQuality ? items[x].ContrastScore : -1,
                    NoiseScore = items[x].HasValidCachedQuality ? items[x].NoiseScore : -1,
                    ResolutionScore = items[x].HasValidCachedQuality ? items[x].ResolutionScore : -1,
                    CompressionScore = items[x].HasValidCachedQuality ? items[x].CompressionScore : -1,
                    FaceCount = items[x].HasValidCachedQuality ? items[x].FaceCount : -1,
                    EyeCount = items[x].HasValidCachedQuality ? items[x].EyeCount : -1,
                    FaceScore = items[x].HasValidCachedQuality ? items[x].FaceScore : -1,
                    EyeScore = items[x].HasValidCachedQuality ? items[x].EyeScore : -1,
                    FacePoseScore = items[x].HasValidCachedQuality ? items[x].FacePoseScore : -1,
                    WorstFaceScore = items[x].HasValidCachedQuality ? items[x].WorstFaceScore : -1,
                    EyeOpennessScore = items[x].HasValidCachedQuality ? items[x].EyeOpennessScore : -1,
                    ClosedEyeCount = items[x].HasValidCachedQuality ? items[x].ClosedEyeCount : -1,
                    BlinkPenalty = items[x].HasValidCachedQuality ? items[x].BlinkPenalty : 0,
                    QualityNotes = items[x].HasValidCachedQuality ? items[x].QualityNotes : ""
                })
                .OrderByDescending(x => x.SimilarityPercent)
                .ThenByDescending(x => (long)x.Width * Math.Max(1, x.Height))
                .ThenByDescending(x => x.FileSize)
                .ToList();

            // Only call a file "recommended" after the whole visual group has a current quality score.
            // A partial cache must never make PAM imply that an unmeasured file is worse.
            if (visualFiles.Count > 0 && visualFiles.All(x => x.HasQuality))
            {
                var best = visualFiles
                    .OrderByDescending(x => x.QualityScore)
                    .ThenBy(x => x.ClosedEyeCount > 0 ? x.ClosedEyeCount : 0)
                    .ThenByDescending(x => x.FaceCount > 0 ? x.WorstFaceScore : -1)
                    .ThenByDescending(x => x.SharpnessScore)
                    .ThenByDescending(x => (long)x.Width * Math.Max(1, x.Height))
                    .ThenByDescending(x => x.FileSize)
                    .First();
                best.IsRecommended = true;
                visualFiles = visualFiles
                    .OrderByDescending(x => x.IsRecommended)
                    .ThenByDescending(x => x.QualityScore)
                    .ThenByDescending(x => x.SimilarityPercent)
                    .ToList();
            }

            result.Add(new VisualDuplicateGroupItem
            {
                GroupKey = items[representativeIndex].Id + ":" + items[representativeIndex].DHash,
                Files = visualFiles,
                Threshold = threshold
            });
        }

        return result
            .OrderByDescending(x => x.FileCount)
            .ThenByDescending(x => x.TotalBytes)
            .ToList();
    }

    private static bool TryParseHash(string value, out ulong hash) =>
        ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out hash);

    private static double AspectRatio(PerceptualHashCandidate item)
    {
        if (item.Width <= 0 || item.Height <= 0) return 0;
        return ExifOrientationHelper.SwapsDimensions(item.Orientation)
            ? item.Height / (double)item.Width
            : item.Width / (double)item.Height;
    }

    private static bool AspectCompatible(PerceptualHashCandidate a, PerceptualHashCandidate b)
    {
        var ra = AspectRatio(a);
        var rb = AspectRatio(b);
        if (ra <= 0 || rb <= 0) return true;
        return Math.Abs(Math.Log(ra / rb)) <= 0.04; // about 4% ratio difference: slight crop is allowed
    }

    private static bool AreKnownExactDuplicates(PerceptualHashCandidate a, PerceptualHashCandidate b) =>
        !string.IsNullOrWhiteSpace(a.Sha256) &&
        !string.IsNullOrWhiteSpace(b.Sha256) &&
        string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase);

    private static long MakeBucketKey(int aspectBucket, int band, byte value)
    {
        unchecked
        {
            var aspect = (uint)(aspectBucket + 100000);
            return ((long)aspect << 16) | ((long)(band & 0xFF) << 8) | value;
        }
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, byte[] rank, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a == b) return;
        if (rank[a] < rank[b]) parent[a] = b;
        else if (rank[a] > rank[b]) parent[b] = a;
        else
        {
            parent[b] = a;
            rank[a]++;
        }
    }
}
