using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class QuarantineService
{
    private readonly DatabaseService _database;
    private readonly string _root;

    public QuarantineService(DatabaseService database, string root)
    {
        _database = database;
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<QuarantineBatchResult> QuarantineAllExceptAsync(
        DuplicateGroupItem group,
        DuplicateFileItem keeper,
        CancellationToken cancellationToken = default)
    {
        if (group.Files.Count < 2)
            throw new InvalidOperationException("В группе должно быть не меньше двух точных копий.");
        if (!group.Files.Any(x => x.Id == keeper.Id))
            throw new InvalidOperationException("Выбранный сохраняемый файл не относится к этой группе.");
        if (string.IsNullOrWhiteSpace(group.Sha256))
            throw new InvalidOperationException("У группы нет рассчитанного SHA-256.");

        // The keeper is verified first. We never quarantine the other copies if the file
        // that the user wants to keep has disappeared or changed since analysis.
        await VerifyFileAsync(keeper.FullPath, keeper.FileSize, group.Sha256, cancellationToken);

        var candidates = group.Files.Where(x => x.Id != keeper.Id).ToList();
        var result = new QuarantineBatchResult { Requested = candidates.Count };

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await VerifyFileAsync(item.FullPath, item.FileSize, group.Sha256, cancellationToken);
                var destination = BuildQuarantinePath(item);
                await TransferVerifiedAsync(item.FullPath, destination, group.Sha256, cancellationToken);

                try
                {
                    await _database.RecordQuarantineAsync(
                        item.Id,
                        item.FullPath,
                        destination,
                        group.Sha256,
                        item.FileSize,
                        cancellationToken);
                }
                catch
                {
                    // The catalogue must not claim a file is still in the source if the DB update failed.
                    // Best effort rollback of the physical transfer; the original exception is preserved.
                    try { await TransferVerifiedAsync(destination, item.FullPath, group.Sha256, cancellationToken); }
                    catch (Exception rollbackEx) { LoggingService.Error("Quarantine DB rollback failed", rollbackEx); }
                    throw;
                }

                result.Succeeded++;
                result.BytesMoved += item.FileSize;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"Quarantine failed for {item.FullPath}", ex);
                result.Errors.Add($"{item.FullPath}: {ex.Message}");
            }
        }

        return result;
    }


    public async Task<QuarantineBatchResult> QuarantineSelectedVisualAsync(
        VisualDuplicateGroupItem group,
        IReadOnlyList<VisualDuplicateFileItem> keepers,
        IReadOnlyList<VisualDuplicateFileItem> candidates,
        CancellationToken cancellationToken = default)
    {
        if (group.Files.Count < 2)
            throw new InvalidOperationException("В визуальной группе должно быть не меньше двух файлов.");
        if (keepers.Count == 0)
            throw new InvalidOperationException("В визуальной группе не выбран ни один кадр ОСТАВИТЬ.");
        var memberIds = group.Files.Select(x => x.Id).ToHashSet();
        if (keepers.Any(x => !memberIds.Contains(x.Id)))
            throw new InvalidOperationException("Список сохраняемых кадров содержит файл из другой визуальной группы.");
        if (candidates.Count == 0)
            throw new InvalidOperationException("Не отмечено ни одного файла для карантина.");
        if (candidates.Any(x => !memberIds.Contains(x.Id)))
            throw new InvalidOperationException("Список карантина содержит файл из другой визуальной группы.");
        if (keepers.Select(x => x.Id).Intersect(candidates.Select(x => x.Id)).Any())
            throw new InvalidOperationException("Один и тот же кадр нельзя одновременно ОСТАВИТЬ и отправить в карантин.");

        // Visual similarity is NOT used as an integrity guarantee. Every manually kept file
        // is verified against its indexed signature, then every explicitly quarantined file
        // gets a fresh SHA-256 that protects the physical transfer.
        foreach (var keeper in keepers)
            await VerifyIndexedSignatureAsync(keeper.FullPath, keeper.FileSize, keeper.LastWriteUtcTicks, cancellationToken);

        var result = new QuarantineBatchResult { Requested = candidates.Count };
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await VerifyIndexedSignatureAsync(item.FullPath, item.FileSize, item.LastWriteUtcTicks, cancellationToken);
                var freshSha256 = await ComputeSha256Async(item.FullPath, cancellationToken);
                var destination = BuildQuarantinePath(item.Id, item.FileName);
                await TransferVerifiedAsync(item.FullPath, destination, freshSha256, cancellationToken);

                try
                {
                    await _database.RecordQuarantineAsync(
                        item.Id,
                        item.FullPath,
                        destination,
                        freshSha256,
                        item.FileSize,
                        cancellationToken);
                }
                catch
                {
                    try { await TransferVerifiedAsync(destination, item.FullPath, freshSha256, cancellationToken); }
                    catch (Exception rollbackEx) { LoggingService.Error("Visual quarantine DB rollback failed", rollbackEx); }
                    throw;
                }

                result.Succeeded++;
                result.BytesMoved += item.FileSize;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"Visual quarantine failed for {item.FullPath}", ex);
                result.Errors.Add($"{item.FullPath}: {ex.Message}");
            }
        }

        return result;
    }

    public async Task<QuarantineBatchResult> QuarantineSelectedBurstAsync(
        BurstGroupItem group,
        IReadOnlyList<VisualDuplicateFileItem> keepers,
        IReadOnlyList<VisualDuplicateFileItem> candidates,
        CancellationToken cancellationToken = default)
    {
        if (group.Files.Count < 3)
            throw new InvalidOperationException("В серии должно быть не меньше трёх файлов.");
        if (keepers.Count == 0)
            throw new InvalidOperationException("Для серии не выбран ни один кадр ОСТАВИТЬ.");
        if (candidates.Count == 0)
            throw new InvalidOperationException("Не отмечено ни одного кадра для карантина.");

        var memberIds = group.Files.Select(x => x.Id).ToHashSet();
        if (keepers.Any(x => !memberIds.Contains(x.Id)) || candidates.Any(x => !memberIds.Contains(x.Id)))
            throw new InvalidOperationException("Выбран файл, который не относится к этой серии.");
        if (keepers.Select(x => x.Id).Intersect(candidates.Select(x => x.Id)).Any())
            throw new InvalidOperationException("Один и тот же кадр нельзя одновременно ОСТАВИТЬ и отправить в карантин.");

        // A burst recommendation is only a culling aid, never an integrity guarantee.
        // Every manually kept file is first verified against the indexed signature. Each
        // quarantined file then receives a fresh SHA-256 that protects the transfer itself.
        foreach (var keeper in keepers)
            await VerifyIndexedSignatureAsync(keeper.FullPath, keeper.FileSize, keeper.LastWriteUtcTicks, cancellationToken);

        var result = new QuarantineBatchResult { Requested = candidates.Count };
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await VerifyIndexedSignatureAsync(item.FullPath, item.FileSize, item.LastWriteUtcTicks, cancellationToken);
                var freshSha256 = await ComputeSha256Async(item.FullPath, cancellationToken);
                var destination = BuildQuarantinePath(item.Id, item.FileName);
                await TransferVerifiedAsync(item.FullPath, destination, freshSha256, cancellationToken);

                try
                {
                    await _database.RecordQuarantineAsync(
                        item.Id,
                        item.FullPath,
                        destination,
                        freshSha256,
                        item.FileSize,
                        cancellationToken);
                }
                catch
                {
                    try { await TransferVerifiedAsync(destination, item.FullPath, freshSha256, cancellationToken); }
                    catch (Exception rollbackEx) { LoggingService.Error("Burst quarantine DB rollback failed", rollbackEx); }
                    throw;
                }

                result.Succeeded++;
                result.BytesMoved += item.FileSize;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"Burst quarantine failed for {item.FullPath}", ex);
                result.Errors.Add($"{item.FullPath}: {ex.Message}");
            }
        }

        return result;
    }

    public async Task UndoAsync(QuarantineActionItem action, CancellationToken cancellationToken = default)
    {
        if (!action.IsActive)
            throw new InvalidOperationException("Эта операция уже отменена.");
        if (File.Exists(action.OriginalPath))
            throw new IOException("По исходному пути уже существует файл. Он не будет перезаписан: " + action.OriginalPath);
        if (!File.Exists(action.QuarantinePath))
            throw new FileNotFoundException("Файл не найден в карантине.", action.QuarantinePath);

        await VerifyFileAsync(action.QuarantinePath, action.FileSize, action.Sha256, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(action.OriginalPath)!);
        await TransferVerifiedAsync(action.QuarantinePath, action.OriginalPath, action.Sha256, cancellationToken);

        try
        {
            await _database.MarkQuarantineUndoneAsync(action.Id, action.FileId, cancellationToken);
        }
        catch
        {
            // Keep DB and filesystem consistent if the database update fails.
            try { await TransferVerifiedAsync(action.OriginalPath, action.QuarantinePath, action.Sha256, cancellationToken); }
            catch (Exception rollbackEx) { LoggingService.Error("Undo DB rollback failed", rollbackEx); }
            throw;
        }
    }

    public async Task DeletePermanentlyAsync(QuarantineActionItem action, CancellationToken cancellationToken = default)
    {
        if (!action.IsActive)
            throw new InvalidOperationException("Окончательно удалить можно только активный файл из карантина.");
        if (!File.Exists(action.QuarantinePath))
            throw new FileNotFoundException("Файл не найден в карантине.", action.QuarantinePath);

        // Permanent deletion is intentionally limited to a file already isolated in quarantine.
        // Its content is verified immediately before the irreversible operation.
        await VerifyFileAsync(action.QuarantinePath, action.FileSize, action.Sha256, cancellationToken);

        // Two-phase delete: first rename inside quarantine, then commit the audit state,
        // then physically remove the tombstone. If the DB update fails we can still roll back.
        var tombstone = action.QuarantinePath + ".pam-delete-pending-" + Guid.NewGuid().ToString("N");
        File.Move(action.QuarantinePath, tombstone);
        try
        {
            await _database.MarkQuarantinePermanentlyDeletedAsync(action.Id, action.FileId, cancellationToken);
        }
        catch
        {
            try
            {
                if (File.Exists(tombstone) && !File.Exists(action.QuarantinePath))
                    File.Move(tombstone, action.QuarantinePath);
            }
            catch (Exception rollbackEx)
            {
                LoggingService.Error("CRITICAL: permanent-delete DB rollback failed", rollbackEx);
            }
            throw;
        }

        try
        {
            File.Delete(tombstone);
            if (File.Exists(tombstone))
                throw new IOException("Операционная система не удалила временный файл окончательного удаления.");
        }
        catch (Exception ex)
        {
            // Catalogue says deleted, but keeping extra bytes is safer than risking an unjournaled deletion.
            LoggingService.Error("Permanent-delete cleanup failed; tombstone remains at: " + tombstone, ex);
            throw new IOException(
                "Журнал уже отметил файл удалённым, но Windows не смогла очистить временный файл. " +
                "Данные не потеряны дополнительно; проверьте журнал и файл: " + tombstone, ex);
        }
    }

    private string BuildQuarantinePath(DuplicateFileItem item) => BuildQuarantinePath(item.Id, item.FileName);

    private string BuildQuarantinePath(long fileId, string fileName)
    {
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        var folder = Path.Combine(_root, day);
        Directory.CreateDirectory(folder);

        var safeName = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        var unique = Guid.NewGuid().ToString("N")[..10];
        return Path.Combine(folder, $"{fileId}_{stem}_{unique}{ext}");
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    private static Task VerifyIndexedSignatureAsync(
        string path,
        long expectedSize,
        long expectedLastWriteUtcTicks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл не найден.", path);
        var info = new FileInfo(path);
        if (info.Length != expectedSize || info.LastWriteTimeUtc.Ticks != expectedLastWriteUtcTicks)
            throw new IOException("Файл изменился после индексации. Повторно просканируйте библиотеку перед карантином.");
        return Task.CompletedTask;
    }

    private static async Task VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл не найден.", path);

        var info = new FileInfo(path);
        if (info.Length != expectedSize)
            throw new IOException($"Размер файла изменился: ожидалось {expectedSize}, сейчас {info.Length} байт.");

        var actual = await ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("SHA-256 файла изменился после анализа. Операция отменена.");
    }

    private static async Task TransferVerifiedAsync(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("Исходный файл не найден.", source);
        if (File.Exists(destination))
            throw new IOException("Целевой файл уже существует: " + destination);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(source)),
            Path.GetPathRoot(Path.GetFullPath(destination)),
            StringComparison.OrdinalIgnoreCase);

        if (sameVolume)
        {
            File.Move(source, destination);
            try
            {
                var movedHash = await ComputeSha256Async(destination, cancellationToken);
                if (!string.Equals(movedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Проверка SHA-256 после перемещения не пройдена.");
            }
            catch
            {
                try
                {
                    if (!File.Exists(source) && File.Exists(destination))
                        File.Move(destination, source);
                }
                catch (Exception rollbackEx)
                {
                    LoggingService.Error("Same-volume transfer rollback failed", rollbackEx);
                }
                throw;
            }
            return;
        }

        // Cross-volume moves are performed as copy -> verify -> delete.
        var temp = destination + ".pamtmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyFileAsync(source, temp, cancellationToken);
            var copiedHash = await ComputeSha256Async(temp, cancellationToken);
            if (!string.Equals(copiedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Проверка SHA-256 скопированного файла не пройдена.");

            File.Move(temp, destination);
            try
            {
                File.Delete(source);
            }
            catch
            {
                // If the original cannot be removed, undo the new copy so the operation
                // does not silently turn into an unmanaged extra duplicate.
                try { if (File.Exists(destination)) File.Delete(destination); }
                catch (Exception cleanupEx) { LoggingService.Error("Cross-volume cleanup failed", cleanupEx); }
                throw;
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* best effort temp cleanup */ }
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, bufferSize, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
