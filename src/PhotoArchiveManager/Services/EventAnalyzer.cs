using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class EventAnalyzer
{
    private readonly DatabaseService _database;
    private readonly MetadataService _metadata;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _activeCts;

    public bool IsPaused => _pauseGate.IsPaused;

    public EventAnalyzer(DatabaseService database, MetadataService metadata)
    {
        _database = database;
        _metadata = metadata;
    }

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _activeCts?.Cancel();

    public async Task<EventBuildResult> AnalyzeAsync(
        int maxGapMinutes,
        int minPhotos,
        IProgress<EventAnalysisProgress>? progress = null,
        CancellationToken externalCancellationToken = default)
    {
        if (_activeCts is not null)
            throw new InvalidOperationException("Анализ событий уже выполняется.");

        maxGapMinutes = Math.Clamp(maxGapMinutes, 15, 720);
        minPhotos = Math.Clamp(minPhotos, 2, 50);
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        var ct = _activeCts.Token;

        try
        {
            var gpsCandidates = await _database.GetEventMetadataCandidatesAsync(ct);
            var processed = 0;
            var gpsRead = 0;
            var gpsCached = 0;
            var gpsFound = 0;
            var errors = 0;

            if (gpsCandidates.Count == 0)
            {
                gpsCached = 1; // only used to make the status clear: GPS cache is already current.
                progress?.Report(new EventAnalysisProgress(
                    "GPS-метаданные уже в кэше", 0, 0, 0, gpsCached, 0, 0, 0, ""));
            }
            else
            {
                foreach (var item in gpsCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    await _pauseGate.WaitIfPausedAsync(ct);
                    processed++;
                    try
                    {
                        if (!File.Exists(item.FullPath))
                        {
                            errors++;
                            await _database.UpdateGpsMetadataAsync(
                                item.FileId, item.FileSize, item.LastWriteUtcTicks, null, null,
                                "Файл отсутствует во время чтения GPS.", ct);
                        }
                        else
                        {
                            var metadata = await Task.Run(() => _metadata.Read(item.FullPath), ct);
                            gpsRead++;
                            if (metadata.GpsLatitude.HasValue && metadata.GpsLongitude.HasValue)
                                gpsFound++;
                            if (!string.IsNullOrWhiteSpace(metadata.Error)) errors++;
                            await _database.UpdateGpsMetadataAsync(
                                item.FileId, item.FileSize, item.LastWriteUtcTicks,
                                metadata.GpsLatitude, metadata.GpsLongitude, metadata.Error, ct);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors++;
                        LoggingService.Error("Event GPS metadata failed: " + item.FullPath, ex);
                        try
                        {
                            await _database.UpdateGpsMetadataAsync(
                                item.FileId, item.FileSize, item.LastWriteUtcTicks, null, null, ex.Message, ct);
                        }
                        catch { /* do not hide the original per-file failure */ }
                    }

                    if (processed % 10 == 0 || processed == gpsCandidates.Count)
                    {
                        progress?.Report(new EventAnalysisProgress(
                            "Чтение GPS для событий", processed, gpsCandidates.Count, gpsRead, gpsCached,
                            gpsFound, errors, 0, item.FullPath));
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            await _pauseGate.WaitIfPausedAsync(ct);
            progress?.Report(new EventAnalysisProgress(
                "Группировка по времени / GPS / людям", 0, 0, gpsRead, gpsCached, gpsFound, errors, 0, ""));

            var candidates = await _database.GetEventCandidatesAsync(ct);
            var drafts = BuildEventDrafts(candidates, maxGapMinutes, minPhotos, progress, gpsRead, gpsCached, gpsFound, errors, ct);

            ct.ThrowIfCancellationRequested();
            var unreliable = await _database.CountPhotosWithoutReliableCaptureDateAsync(ct);
            var manual = await _database.CountPhotosInManualEventsAsync(ct);
            var saved = await _database.ReplaceAutomaticEventsAsync(drafts, ct);

            progress?.Report(new EventAnalysisProgress(
                "Готово", candidates.Count, candidates.Count, gpsRead, gpsCached, gpsFound, errors,
                saved.EventsCreated, ""));

            return new EventBuildResult(saved.EventsCreated, saved.PhotosAssigned, unreliable, manual);
        }
        finally
        {
            _pauseGate.Resume();
            _activeCts.Dispose();
            _activeCts = null;
        }
    }

    private static List<EventDraft> BuildEventDrafts(
        IReadOnlyList<EventCandidate> candidates,
        int maxGapMinutes,
        int minPhotos,
        IProgress<EventAnalysisProgress>? progress,
        int gpsRead,
        int gpsCached,
        int gpsFound,
        int errors,
        CancellationToken ct)
    {
        var drafts = new List<EventDraft>();
        var current = new List<EventCandidate>();
        var processed = 0;

        foreach (var item in candidates.OrderBy(x => x.CaptureDate).ThenBy(x => x.FileId))
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            if (current.Count == 0 || ShouldJoin(current, item, maxGapMinutes))
            {
                current.Add(item);
            }
            else
            {
                AddDraftIfLargeEnough(current, minPhotos, drafts);
                current = new List<EventCandidate> { item };
            }

            if (processed % 250 == 0 || processed == candidates.Count)
            {
                progress?.Report(new EventAnalysisProgress(
                    "Группировка по времени / GPS / людям", processed, candidates.Count,
                    gpsRead, gpsCached, gpsFound, errors, drafts.Count, item.FullPath));
            }
        }

        AddDraftIfLargeEnough(current, minPhotos, drafts);
        return drafts;
    }

    private static bool ShouldJoin(IReadOnlyList<EventCandidate> current, EventCandidate next, int maxGapMinutes)
    {
        var previous = current[^1];
        var gap = next.CaptureDate - previous.CaptureDate;
        if (gap < TimeSpan.Zero) return false;

        var directLimit = TimeSpan.FromMinutes(maxGapMinutes);
        var sameDay = next.CaptureDate.Date == previous.CaptureDate.Date;
        var distanceKm = TryDistanceKm(previous.Latitude, previous.Longitude, next.Latitude, next.Longitude);

        // Strong location contradiction: two GPS points very far apart are split even if timestamps are close.
        if (distanceKm.HasValue && distanceKm.Value > 80 && gap <= TimeSpan.FromHours(3))
            return false;

        if (gap <= directLimit)
            return true;

        var personOverlap = HasPersonOverlap(current, next);
        var clusterDistanceKm = DistanceToClusterKm(current, next);
        var locationBridge = clusterDistanceKm.HasValue && clusterDistanceKm.Value <= 5;

        // Conservative bridge for a meal/museum/walk with a longer camera pause on the same day.
        var bridgeLimit = TimeSpan.FromMinutes(Math.Min(360, maxGapMinutes * 2));
        if (sameDay && gap <= bridgeLimit && (locationBridge || personOverlap))
            return true;

        // Crossing midnight can still be one event, but only with corroborating people/location.
        if (!sameDay && gap <= TimeSpan.FromHours(3) && (locationBridge || personOverlap))
            return true;

        return false;
    }

    private static bool HasPersonOverlap(IReadOnlyList<EventCandidate> current, EventCandidate next)
    {
        if (next.PersonIds.Count == 0) return false;
        // Recent members carry more context and prevent a person seen once many hours ago from bridging forever.
        foreach (var item in current.Skip(Math.Max(0, current.Count - 12)))
            if (item.PersonIds.Any(next.PersonIds.Contains))
                return true;
        return false;
    }

    private static double? DistanceToClusterKm(IReadOnlyList<EventCandidate> current, EventCandidate next)
    {
        if (!next.Latitude.HasValue || !next.Longitude.HasValue) return null;
        var points = current
            .Where(x => x.Latitude.HasValue && x.Longitude.HasValue)
            .TakeLast(20)
            .ToList();
        if (points.Count == 0) return null;
        var lat = points.Average(x => x.Latitude!.Value);
        var lon = points.Average(x => x.Longitude!.Value);
        return HaversineKm(lat, lon, next.Latitude.Value, next.Longitude.Value);
    }

    private static double? TryDistanceKm(double? lat1, double? lon1, double? lat2, double? lon2)
    {
        if (!lat1.HasValue || !lon1.HasValue || !lat2.HasValue || !lon2.HasValue) return null;
        return HaversineKm(lat1.Value, lon1.Value, lat2.Value, lon2.Value);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radiusKm = 6371.0088;
        static double Rad(double degrees) => degrees * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
    }

    private static void AddDraftIfLargeEnough(IReadOnlyList<EventCandidate> group, int minPhotos, ICollection<EventDraft> drafts)
    {
        if (group.Count < minPhotos) return;
        var start = group[0].CaptureDate;
        var end = group[^1].CaptureDate;
        var gps = group.Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToList();
        double? centerLat = gps.Count == 0 ? null : gps.Average(x => x.Latitude!.Value);
        double? centerLon = gps.Count == 0 ? null : gps.Average(x => x.Longitude!.Value);

        var people = group.SelectMany(x => x.PersonNames)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Key)
            .Take(2)
            .ToList();

        var confidence = CalculateConfidence(group);
        drafts.Add(new EventDraft
        {
            Name = BuildAutoName(start, end, people),
            StartDate = start,
            EndDate = end,
            Confidence = confidence,
            CenterLatitude = centerLat,
            CenterLongitude = centerLon,
            FileIds = group.Select(x => x.FileId).Distinct().ToList()
        });
    }

    private static string BuildAutoName(DateTime start, DateTime end, IReadOnlyList<string> people)
    {
        string name;
        if (start.Date == end.Date)
            name = $"{start:dd.MM.yyyy} · {start:HH:mm}–{end:HH:mm}";
        else if (start.Year == end.Year)
            name = $"{start:dd.MM}–{end:dd.MM.yyyy}";
        else
            name = $"{start:dd.MM.yyyy}–{end:dd.MM.yyyy}";

        // People are shown dynamically in the event UI. Keep the automatic name date-based so
        // renaming a person later cannot make a stored event title stale.
        return name;
    }

    private static double CalculateConfidence(IReadOnlyList<EventCandidate> group)
    {
        if (group.Count < 2) return 0.45;
        var score = 0.58;
        var gaps = new List<double>();
        for (var i = 1; i < group.Count; i++)
            gaps.Add(Math.Max(0, (group[i].CaptureDate - group[i - 1].CaptureDate).TotalMinutes));
        var averageGap = gaps.Count == 0 ? 0 : gaps.Average();
        if (averageGap <= 15) score += 0.14;
        else if (averageGap <= 45) score += 0.08;

        var gpsCount = group.Count(x => x.Latitude.HasValue && x.Longitude.HasValue);
        if (gpsCount >= Math.Min(3, group.Count)) score += 0.10;

        var namedPeopleCount = group.SelectMany(x => x.PersonIds).Distinct().Count();
        if (namedPeopleCount > 0) score += 0.07;

        if (group.Count >= 5) score += 0.05;
        return Math.Clamp(score, 0.40, 0.98);
    }
}
