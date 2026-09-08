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
    public bool HadIncompleteTraversal { get; private set; }

    public LibraryScanner(DatabaseService database, MetadataService metadata, ThumbnailService thumbnails)
    {
        _database = database;
        _metadata = metadata;
        _thumbnails = thumbnails;
    }


    private static bool IsPamInternalArtifact(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;

        // PAM may briefly create hidden/pending files while a verified move or delete is being
        // finalized. They must never become catalog photos, including leftovers from an older
        // version that used the original image extension for a source tombstone.
        return name.StartsWith(".pam-source-pending-", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".pam-delete-pending-", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".pamtmp-", StringComparison.OrdinalIgnoreCase);
    }

    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _activeCts?.Cancel();

    public async Task ScanAsync(IReadOnlyList<string> sourceFolders, IProgress<ScanProgress>? progress, CancellationToken externalCancellationToken)
    {
        if (_activeCts is not null) throw new InvalidOperationException("Сканирование уже выполняется.");
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        var ct = _activeCts.Token;
        HadIncompleteTraversal = false;

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
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        progress?.Report(new ScanProgress("Поиск файлов", 0, 0, 0, 0, 0, source));

        // Directory enumeration is synchronous. Walk it on a worker thread so a large tree cannot
        // freeze WPF. Traverse directory-by-directory instead of using IgnoreInaccessible=true:
        // that flag silently skips an unreadable subtree and would make its already indexed photos
        // look deleted. Here we keep scanning other readable branches and remember that the walk was
        // incomplete, so missing-state reconciliation is skipped for this source.
        var enumeration = await Task.Run(async () =>
        {
            var found = new List<string>();
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(source);
            var completed = true;

            while (pendingDirectories.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(ct).ConfigureAwait(false);
                var directory = pendingDirectories.Pop();

                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        await _pauseGate.WaitIfPausedAsync(ct).ConfigureAwait(false);
                        if (IsPamInternalArtifact(file)) continue;
                        if (!SupportedExtensions.Contains(Path.GetExtension(file))) continue;

                        found.Add(file);
                        if (found.Count % 500 == 0)
                            progress?.Report(new ScanProgress("Поиск файлов", found.Count, 0, 0, 0, 0, file));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    completed = false;
                    LoggingService.Warn($"File enumeration warning for {directory}: {ex.Message}");
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
                        pendingDirectories.Push(child);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    completed = false;
                    LoggingService.Warn($"Directory enumeration warning for {directory}: {ex.Message}");
                }
            }

            return (Files: found, Completed: completed);
        }, ct);
        var files = enumeration.Files;

        var signatures = await _database.GetSignaturesAsync(source, ct);
        var processed = 0;
        var indexed = 0;
        var skipped = 0;
        var errors = enumeration.Completed ? 0 : 1;

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        SqliteTransaction? transaction = null;
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
                    // Reading these properties forces FileInfo to touch the filesystem. Keep the old
                    // signature in the missing-candidate set until that presence check has succeeded.
                    var fileSize = info.Length;
                    var lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
                    var hadSignature = signatures.TryGetValue(file, out var signature);

                    // The path is physically present. From this point a metadata/decoder error is an
                    // indexing error, not evidence that the catalog file is missing.
                    signatures.Remove(file);

                    if (hadSignature &&
                        signature is not null &&
                        signature.FileSize == fileSize &&
                        signature.LastWriteUtcTicks == lastWriteUtcTicks &&
                        !signature.HasIndexError)
                    {
                        skipped++;
                    }
                    else
                    {
                        var metadata = await Task.Run(() => _metadata.Read(file), ct);
                        var thumbnail = await _thumbnails.CreateAsync(file, fileSize, lastWriteUtcTicks, metadata.Orientation, ct);

                        // Do not commit metadata/preview assembled from a file that changed midway.
                        // This is especially important for photos still being copied into a watched folder.
                        info.Refresh();
                        if (!info.Exists || info.Length != fileSize || info.LastWriteTimeUtc.Ticks != lastWriteUtcTicks)
                            throw new IOException("Файл изменился во время индексации. Он будет обработан при следующем сканировании.");

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
                            FileSize = fileSize,
                            LastWriteUtcTicks = lastWriteUtcTicks,
                            CreationUtcTicks = info.CreationTimeUtc.Ticks,
                            CaptureDate = StoredDateTime.Format(captureDate.Value),
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

                        transaction ??= connection.BeginTransaction();
                        await _database.UpsertPhotoAsync(connection, transaction, record, ct);
                        indexed++;
                        batchCount++;

                        if (batchCount >= 200)
                        {
                            await transaction.CommitAsync(ct);
                            await transaction.DisposeAsync();
                            transaction = null;
                            batchCount = 0;
                        }
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

            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
                await transaction.DisposeAsync();
                transaction = null;
            }

            if (enumeration.Completed)
            {
                await _database.CompleteSourceScanAsync(source, signatures.Keys.ToArray(), ct);
                progress?.Report(new ScanProgress("Готово", files.Count, processed, indexed, skipped, errors, source));
            }
            else
            {
                HadIncompleteTraversal = true;
                // Never infer "missing" from a partial traversal: a transient I/O/access failure can
                // make a perfectly healthy subtree temporarily invisible. Successfully indexed rows
                // above stay saved, but absence is only committed after a complete walk.
                progress?.Report(new ScanProgress("Обход завершён не полностью — статус отсутствующих файлов не менялся", files.Count, processed, indexed, skipped, errors, source));
            }
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }
}
