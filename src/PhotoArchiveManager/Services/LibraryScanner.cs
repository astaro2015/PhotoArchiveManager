using Microsoft.Data.Sqlite;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class LibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".gif"
    };

    private readonly DatabaseService _database;
    private readonly MetadataService _metadata;
    private readonly ThumbnailService _thumbnails;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _activeCts;

    public bool IsPaused => _pauseGate.IsPaused;

    public LibraryScanner(DatabaseService database, MetadataService metadata, ThumbnailService thumbnails)
    {
        _database = database;
        _metadata = metadata;
        _thumbnails = thumbnails;
    }

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _activeCts?.Cancel();

    public async Task ScanAsync(IReadOnlyList<string> sourceFolders, IProgress<ScanProgress>? progress, CancellationToken externalCancellationToken)
    {
        if (_activeCts is not null) throw new InvalidOperationException("Сканирование уже выполняется.");
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        var ct = _activeCts.Token;

        try
        {
            foreach (var source in sourceFolders)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(source))
                {
                    LoggingService.Warn("Source folder not found: " + source);
                    continue;
                }
                await ScanOneSourceAsync(source, progress, ct);
            }
        }
        finally
        {
            _pauseGate.Resume();
            _activeCts.Dispose();
            _activeCts = null;
        }
    }

    private async Task ScanOneSourceAsync(string source, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var scanId = Guid.NewGuid().ToString("N");
        var files = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        progress?.Report(new ScanProgress("Поиск файлов", 0, 0, 0, 0, 0, source));
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(ct);
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                {
                    files.Add(file);
                    if (files.Count % 500 == 0)
                        progress?.Report(new ScanProgress("Поиск файлов", files.Count, 0, 0, 0, 0, file));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            LoggingService.Warn($"Enumeration warning for {source}: {ex.Message}");
        }

        var signatures = await _database.GetSignaturesAsync(source, ct);
        var processed = 0;
        var indexed = 0;
        var skipped = 0;
        var errors = 0;

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        SqliteTransaction transaction = connection.BeginTransaction();
        var batchCount = 0;

        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(ct);
                processed++;

                try
                {
                    var info = new FileInfo(file);
                    if (signatures.TryGetValue(file, out var signature) &&
                        signature.FileSize == info.Length &&
                        signature.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
                    {
                        await _database.TouchUnchangedAsync(connection, transaction, file, scanId, ct);
                        skipped++;
                    }
                    else
                    {
                        var metadata = await Task.Run(() => _metadata.Read(file), ct);
                        var thumbnail = await _thumbnails.CreateAsync(file, info.Length, info.LastWriteTimeUtc.Ticks, metadata.Orientation, ct);
                        var captureDate = metadata.CaptureDate;
                        var captureSource = metadata.CaptureDateSource;
                        if (!captureDate.HasValue)
                        {
                            captureDate = info.LastWriteTime;
                            captureSource = "Дата файла (fallback)";
                        }

                        var error = string.Join(" | ", new[] { metadata.Error, thumbnail.Error }.Where(x => !string.IsNullOrWhiteSpace(x)));
                        if (!string.IsNullOrWhiteSpace(error)) errors++;

                        var record = new PhotoRecord
                        {
                            FullPath = file,
                            SourceFolder = source,
                            FileName = info.Name,
                            Extension = info.Extension.ToLowerInvariant(),
                            FileSize = info.Length,
                            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                            CreationUtcTicks = info.CreationTimeUtc.Ticks,
                            CaptureDate = captureDate.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                            CaptureDateSource = captureSource,
                            EffectiveYear = captureDate.Value.Year,
                            Width = thumbnail.Width,
                            Height = thumbnail.Height,
                            CameraMake = metadata.CameraMake,
                            CameraModel = metadata.CameraModel,
                            Orientation = metadata.Orientation,
                            GpsLatitude = metadata.GpsLatitude,
                            GpsLongitude = metadata.GpsLongitude,
                            ThumbnailPath = thumbnail.ThumbnailPath,
                            Error = error,
                            LastSeenScanId = scanId
                        };
                        await _database.UpsertPhotoAsync(connection, transaction, record, ct);
                        indexed++;
                    }

                    batchCount++;
                    if (batchCount >= 200)
                    {
                        await transaction.CommitAsync(ct);
                        await transaction.DisposeAsync();
                        transaction = connection.BeginTransaction();
                        batchCount = 0;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    LoggingService.Error("Failed to index: " + file, ex);
                }

                if (processed % 10 == 0 || processed == files.Count)
                    progress?.Report(new ScanProgress("Индексация", files.Count, processed, indexed, skipped, errors, file));
            }

            await transaction.CommitAsync(ct);
            await transaction.DisposeAsync();
            transaction = null!;
            await _database.CompleteSourceScanAsync(source, scanId, ct);
            progress?.Report(new ScanProgress("Готово", files.Count, processed, indexed, skipped, errors, source));
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }
}
