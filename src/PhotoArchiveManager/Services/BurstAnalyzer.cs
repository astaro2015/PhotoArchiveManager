using System.Numerics;
using Microsoft.Data.Sqlite;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class BurstAnalyzer
{
    private readonly DatabaseService _database;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _internalCts;

    public bool IsPaused => _pauseGate.IsPaused;

    public BurstAnalyzer(DatabaseService database) => _database = database;

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _internalCts?.Cancel();

    public async Task<IReadOnlyList<BurstGroupItem>> AnalyzeAsync(
        int maxGapSeconds,
        int minGroupSize,
        int keepCount,
        IProgress<BurstProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        maxGapSeconds = Math.Clamp(maxGapSeconds, 2, 120);
        minGroupSize = Math.Clamp(minGroupSize, 3, 20);
        keepCount = Math.Clamp(keepCount, 1, 3);

        if (_internalCts is not null)
            throw new InvalidOperationException("Поиск серий уже выполняется.");

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _internalCts.Token;
        _pauseGate.Resume();

        try
        {
            var candidates = (await _database.GetPerceptualHashCandidatesAsync(token))
                .Where(x => TryCaptureDate(x, out _))
                .ToList();

            var total = candidates.Count;
            var processed = 0;
            var computed = 0;
            var cached = 0;
            var errors = 0;

            progress?.Report(new BurstProgress
            {
                Stage = "Подготовка отпечатков для серий",
                TotalFiles = total
            });

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
                        progress?.Report(new BurstProgress
                        {
                            Stage = "Расчёт отпечатков для серий",
                            CurrentFile = item.FullPath,
                            TotalFiles = total,
                            ProcessedFiles = processed,
                            ComputedHashes = computed,
                            CachedHashes = cached,
                            ErrorFiles = errors
                        });

                        var hashes = await Task.Run(() => PerceptualHashAnalyzer.ComputeHashes(item.FullPath, item.Orientation), token);
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
                    LoggingService.Error("Burst hash failed: " + item.FullPath, ex);
                    await _database.UpdatePerceptualHashAsync(
                        connection, item.Id, "", "", item.FileSize, item.LastWriteUtcTicks, ex.Message, token);
                }

                processed++;
                if (processed % 25 == 0 || processed == total)
                {
                    progress?.Report(new BurstProgress
                    {
                        Stage = "Подготовка серий",
                        CurrentFile = item.FullPath,
                        TotalFiles = total,
                        ProcessedFiles = processed,
                        ComputedHashes = computed,
                        CachedHashes = cached,
                        ErrorFiles = errors
                    });
                }
            }

            progress?.Report(new BurstProgress
            {
                Stage = "Группировка серий",
                TotalFiles = total,
                ProcessedFiles = processed,
                ComputedHashes = computed,
                CachedHashes = cached,
                ErrorFiles = errors
            });

            var refreshed = await _database.GetPerceptualHashCandidatesAsync(token);
            var groups = await Task.Run(
                () => BuildGroups(refreshed, maxGapSeconds, minGroupSize, keepCount, token),
                token);

            progress?.Report(new BurstProgress
            {
                Stage = "Готово",
                TotalFiles = total,
                ProcessedFiles = processed,
                ComputedHashes = computed,
                CachedHashes = cached,
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

    public async Task<IReadOnlyList<BurstGroupItem>> LoadCachedGroupsAsync(
        int maxGapSeconds,
        int minGroupSize,
        int keepCount,
        CancellationToken cancellationToken = default)
    {
        maxGapSeconds = Math.Clamp(maxGapSeconds, 2, 120);
        minGroupSize = Math.Clamp(minGroupSize, 3, 20);
        keepCount = Math.Clamp(keepCount, 1, 3);
        var candidates = await _database.GetPerceptualHashCandidatesAsync(cancellationToken);
        return await Task.Run(
            () => BuildGroups(candidates, maxGapSeconds, minGroupSize, keepCount, cancellationToken),
            cancellationToken);
    }

    private static List<BurstGroupItem> BuildGroups(
        IReadOnlyList<PerceptualHashCandidate> source,
        int maxGapSeconds,
        int minGroupSize,
        int keepCount,
        CancellationToken token)
    {
        // Exact file copies must not inflate a burst. When a current SHA-256 is known,
        // only one physical copy participates in derived series grouping.
        var items = source
            .Where(x => x.HasValidCachedHash && TryCaptureDate(x, out _))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Sha256) ? "id:" + x.Id : "sha:" + x.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.FileSize).ThenBy(x => x.Id).First())
            .Select(x => new TimedCandidate(x, ParseCaptureDate(x)))
            .OrderBy(x => x.Time)
            .ThenBy(x => x.Item.Id)
            .ToList();

        if (items.Count < minGroupSize) return new List<BurstGroupItem>();

        var result = new List<BurstGroupItem>();
        var current = new List<TimedCandidate>();

        void FlushCurrent()
        {
            if (current.Count >= minGroupSize)
            {
                var group = CreateGroup(current, keepCount);
                if (group is not null) result.Add(group);
            }
            current = new List<TimedCandidate>();
        }

        foreach (var candidate in items)
        {
            token.ThrowIfCancellationRequested();
            if (current.Count == 0)
            {
                current.Add(candidate);
                continue;
            }

            var previous = current[^1];
            var gap = (candidate.Time - previous.Time).TotalSeconds;
            var totalDuration = (candidate.Time - current[0].Time).TotalSeconds;

            // Chain only photos that are close in time and still visually related.
            // A 90 s hard cap prevents a long sightseeing walk from becoming one huge "series"
            // merely because the shutter was pressed every few seconds.
            var canJoin = gap >= 0 && gap <= maxGapSeconds && totalDuration <= 90 &&
                          CameraCompatible(previous.Item, candidate.Item) &&
                          AspectCompatible(previous.Item, candidate.Item) &&
                          VisuallyRelated(previous.Item, candidate.Item);

            // One unusually changed frame inside a real burst should not always split it.
            // If the previous comparison fails, also compare against the penultimate frame.
            if (!canJoin && current.Count >= 2 && gap >= 0 && gap <= maxGapSeconds && totalDuration <= 90)
            {
                var penultimate = current[^2];
                canJoin = CameraCompatible(penultimate.Item, candidate.Item) &&
                          AspectCompatible(penultimate.Item, candidate.Item) &&
                          VisuallyRelated(penultimate.Item, candidate.Item);
            }

            if (canJoin)
                current.Add(candidate);
            else
            {
                FlushCurrent();
                current.Add(candidate);
            }
        }
        FlushCurrent();

        return result
            .OrderByDescending(x => x.StartDate)
            .ThenByDescending(x => x.FileCount)
            .ToList();
    }

    private static BurstGroupItem? CreateGroup(List<TimedCandidate> timed, int keepCount)
    {
        if (timed.Count == 0) return null;
        var representative = timed
            .Select(x => x.Item)
            .OrderByDescending(x => (long)x.Width * Math.Max(1, x.Height))
            .ThenByDescending(x => x.FileSize)
            .First();

        if (!TryHash(representative.DHash, out var repD) || !TryHash(representative.AHash, out var repA))
            return null;

        var files = timed
            .Select(t =>
            {
                TryHash(t.Item.DHash, out var d);
                TryHash(t.Item.AHash, out var a);
                var quality = t.Item.HasValidCachedQuality;
                return new VisualDuplicateFileItem
                {
                    Id = t.Item.Id,
                    FullPath = t.Item.FullPath,
                    FileName = t.Item.FileName,
                    SourceFolder = t.Item.SourceFolder,
                    ThumbnailPath = t.Item.ThumbnailPath,
                    FileSize = t.Item.FileSize,
                    LastWriteUtcTicks = t.Item.LastWriteUtcTicks,
                    Width = t.Item.Width,
                    Height = t.Item.Height,
                    Orientation = t.Item.Orientation,
                    CaptureDate = t.Item.CaptureDate,
                    CameraMake = t.Item.CameraMake,
                    CameraModel = t.Item.CameraModel,
                    Sha256 = t.Item.Sha256,
                    DHash = t.Item.DHash,
                    AHash = t.Item.AHash,
                    DistanceFromRepresentative = BitOperations.PopCount(d ^ repD),
                    AverageHashDistanceFromRepresentative = BitOperations.PopCount(a ^ repA),
                    HasQuality = quality,
                    QualityScore = quality ? t.Item.QualityScore : -1,
                    SharpnessScore = quality ? t.Item.SharpnessScore : -1,
                    BlurScore = quality ? t.Item.BlurScore : -1,
                    ExposureScore = quality ? t.Item.ExposureScore : -1,
                    ResolutionScore = quality ? t.Item.ResolutionScore : -1,
                    CompressionScore = quality ? t.Item.CompressionScore : -1,
                    FaceCount = quality ? t.Item.FaceCount : -1,
                    EyeCount = quality ? t.Item.EyeCount : -1,
                    FaceScore = quality ? t.Item.FaceScore : -1,
                    EyeScore = quality ? t.Item.EyeScore : -1,
                    QualityNotes = quality ? t.Item.QualityNotes : ""
                };
            })
            .OrderBy(x => ParseCaptureDate(x.CaptureDate))
            .ThenBy(x => x.Id)
            .ToList();

        var group = new BurstGroupItem
        {
            GroupKey = $"{timed[0].Item.Id}:{timed[0].Time:yyyyMMddHHmmss}",
            StartDate = timed[0].Time,
            EndDate = timed[^1].Time,
            Files = files
        };
        group.ApplyRecommendations(keepCount);
        return group;
    }

    private static bool VisuallyRelated(PerceptualHashCandidate a, PerceptualHashCandidate b)
    {
        if (!TryHash(a.DHash, out var ad) || !TryHash(b.DHash, out var bd) ||
            !TryHash(a.AHash, out var aa) || !TryHash(b.AHash, out var ba))
            return false;

        var d = BitOperations.PopCount(ad ^ bd);
        var average = BitOperations.PopCount(aa ^ ba);

        // Much looser than the near-duplicate detector: adjacent burst frames may contain
        // facial expression, pose and camera movement changes, while still representing one moment.
        return d <= 20 && average <= 24;
    }

    private static bool CameraCompatible(PerceptualHashCandidate a, PerceptualHashCandidate b)
    {
        var ca = (a.CameraMake + " " + a.CameraModel).Trim();
        var cb = (b.CameraMake + " " + b.CameraModel).Trim();
        if (string.IsNullOrWhiteSpace(ca) || string.IsNullOrWhiteSpace(cb)) return true;
        return string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AspectCompatible(PerceptualHashCandidate a, PerceptualHashCandidate b)
    {
        var ra = AspectRatio(a);
        var rb = AspectRatio(b);
        if (ra <= 0 || rb <= 0) return true;
        return Math.Abs(Math.Log(ra / rb)) <= 0.08; // about 8% difference: allow modest crop/rotation normalization
    }

    private static double AspectRatio(PerceptualHashCandidate item)
    {
        if (item.Width <= 0 || item.Height <= 0) return 0;
        return item.Orientation is 6 or 8
            ? item.Height / (double)item.Width
            : item.Width / (double)item.Height;
    }

    private static bool TryHash(string value, out ulong hash) =>
        ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out hash);

    private static bool TryCaptureDate(PerceptualHashCandidate item, out DateTime value)
    {
        value = default;
        // File-system fallback dates are intentionally excluded from automatic burst detection.
        // EXIF and an explicit catalog-only manual correction are trusted.
        // Copying an old archive can give thousands of unrelated files nearly identical mtimes.
        if (!CaptureDatePolicy.IsTrusted(item.CaptureDateSource)) return false;
        return DateTime.TryParse(item.CaptureDate, out value);
    }

    private static DateTime ParseCaptureDate(PerceptualHashCandidate item) =>
        DateTime.TryParse(item.CaptureDate, out var value) ? value : DateTime.MinValue;

    private static DateTime ParseCaptureDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;

    private sealed record TimedCandidate(PerceptualHashCandidate Item, DateTime Time);
}
