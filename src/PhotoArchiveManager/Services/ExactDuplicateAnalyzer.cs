using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class ExactDuplicateAnalyzer
{
    private readonly DatabaseService _database;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _activeCts;

    public bool IsPaused => _pauseGate.IsPaused;

    public ExactDuplicateAnalyzer(DatabaseService database)
    {
        _database = database;
    }

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _activeCts?.Cancel();

    public async Task AnalyzeAsync(IProgress<ExactDuplicateProgress>? progress, CancellationToken externalCancellationToken)
    {
        if (_activeCts is not null)
            throw new InvalidOperationException("Поиск точных дублей уже выполняется.");

        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        var ct = _activeCts.Token;

        try
        {
            progress?.Report(new ExactDuplicateProgress("Поиск кандидатов одинакового размера", 0, 0, 0, 0, 0, 0, 0, ""));
            var candidates = await _database.GetHashCandidatesAsync(ct);
            var totalBytes = candidates.Sum(x => x.FileSize);
            var processedBytes = 0L;
            var processed = 0;
            var hashed = 0;
            var cached = 0;
            var errors = 0;

            await using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct);

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(ct);

                var stage = "SHA-256";
                try
                {
                    if (!File.Exists(candidate.FullPath))
                    {
                        errors++;
                        await _database.UpdateHashAsync(connection, candidate.Id, "", candidate.FileSize, candidate.LastWriteUtcTicks,
                            "Файл отсутствует. Выполните повторное сканирование библиотеки.", ct);
                    }
                    else
                    {
                        var info = new FileInfo(candidate.FullPath);
                        if (info.Length != candidate.FileSize || info.LastWriteTimeUtc.Ticks != candidate.LastWriteUtcTicks)
                        {
                            errors++;
                            await _database.UpdateHashAsync(connection, candidate.Id, "", candidate.FileSize, candidate.LastWriteUtcTicks,
                                "Файл изменился после индексации. Выполните повторное сканирование библиотеки.", ct);
                        }
                        else if (!string.IsNullOrWhiteSpace(candidate.Sha256) &&
                                 candidate.HashFileSize == candidate.FileSize &&
                                 candidate.HashLastWriteUtcTicks == candidate.LastWriteUtcTicks)
                        {
                            cached++;
                            stage = "SHA-256 (из кэша)";
                        }
                        else
                        {
                            var hash = await ComputeSha256Async(candidate.FullPath, ct);
                            await _database.UpdateHashAsync(connection, candidate.Id, hash,
                                candidate.FileSize, candidate.LastWriteUtcTicks, "", ct);
                            hashed++;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors++;
                    LoggingService.Error("Failed to hash: " + candidate.FullPath, ex);
                    try
                    {
                        await _database.UpdateHashAsync(connection, candidate.Id, "", candidate.FileSize, candidate.LastWriteUtcTicks, ex.Message, ct);
                    }
                    catch (Exception dbEx)
                    {
                        LoggingService.Error("Failed to save hash error: " + candidate.FullPath, dbEx);
                    }
                }

                processed++;
                processedBytes += candidate.FileSize;
                // Cached candidates can be traversed much faster than the WPF dispatcher can paint
                // progress text. Do not enqueue thousands of redundant UI updates on a warm rerun.
                if (stage != "SHA-256 (из кэша)" || processed % 20 == 0 || processed == candidates.Count)
                {
                    progress?.Report(new ExactDuplicateProgress(stage, candidates.Count, processed, hashed, cached,
                        errors, processedBytes, totalBytes, candidate.FullPath));
                }
            }

            progress?.Report(new ExactDuplicateProgress("Группировка точных дублей", candidates.Count, processed,
                hashed, cached, errors, processedBytes, totalBytes, ""));

            progress?.Report(new ExactDuplicateProgress("Готово", candidates.Count, processed, hashed, cached,
                errors, processedBytes, totalBytes, ""));
        }
        finally
        {
            _pauseGate.Resume();
            _activeCts.Dispose();
            _activeCts = null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var sha = SHA256.Create();
        var bytes = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(bytes);
    }
}
