using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class QuarantineService
{
    private readonly DatabaseService _database;
    private readonly string _root;

    public QuarantineService(DatabaseService database, string root)
    {
        _database = database;
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public Task<QuarantineBatchResult> QuarantineAllExceptAsync(
        DuplicateGroupItem group,
        DuplicateFileItem keeper,
        CancellationToken cancellationToken = default)
    {
        var candidates = group.Files.Where(x => x.Id != keeper.Id).ToList();
        return QuarantineExactCandidatesAsync(group, keeper, candidates, cancellationToken);
    }

    public async Task<QuarantineBatchResult> QuarantineExactCandidatesAsync(
        DuplicateGroupItem group,
        DuplicateFileItem keeper,
        IReadOnlyList<DuplicateFileItem> candidates,
        CancellationToken cancellationToken = default)
    {
        if (group.Files.Count < 2)
            throw new InvalidOperationException("В группе должно быть не меньше двух точных копий.");
        if (!group.Files.Any(x => x.Id == keeper.Id))
            throw new InvalidOperationException("Выбранный сохраняемый файл не относится к этой группе.");
        if (string.IsNullOrWhiteSpace(group.Sha256))
            throw new InvalidOperationException("У группы нет рассчитанного SHA-256.");

        var memberIds = group.Files.Select(x => x.Id).ToHashSet();
        var uniqueCandidates = candidates
            .Where(x => x.Id != keeper.Id)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
        if (uniqueCandidates.Any(x => !memberIds.Contains(x.Id)))
            throw new InvalidOperationException("Список карантина содержит файл из другой группы точных дублей.");

        // Hold the verified keeper open for the whole batch, just like irreversible deletion.
        // This prevents a concurrent rename/delete/replacement from making the active library lose
        // its last confirmed copy while candidates are being moved to quarantine.
        EnsureLibraryFilePathSafe(keeper.FullPath, keeper.SourceFolder);
        await using var keeperLock = await OpenVerifiedReadLockAsync(
            keeper.FullPath, keeper.FileSize, group.Sha256, cancellationToken);

        var result = new QuarantineBatchResult { Requested = uniqueCandidates.Count };
        foreach (var item in uniqueCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureLibraryFilePathSafe(item.FullPath, item.SourceFolder);
                await VerifyFileAsync(item.FullPath, item.FileSize, group.Sha256, cancellationToken);
                var destination = BuildQuarantinePath(item);
                var transfer = await TransferVerifiedAsync(item.FullPath, destination, group.Sha256, cancellationToken);

                try
                {
                    await _database.RecordQuarantineAsync(
                        item.Id,
                        item.FullPath,
                        destination,
                        group.Sha256,
                        item.FileSize,
                        transfer.SourceLastWriteUtc.Ticks,
                        transfer.SourceCreationUtc.Ticks,
                        CancellationToken.None);
                }
                catch
                {
                    // The catalogue must not claim a file is still in the source if the DB update failed.
                    // Best effort rollback of the physical transfer; the original exception is preserved.
                    try
                    {
                        await TransferVerifiedAsync(
                            destination, item.FullPath, group.Sha256, CancellationToken.None,
                            transfer.SourceCreationUtc, transfer.SourceLastWriteUtc);
                    }
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


    public Task<ExactDeleteBatchResult> DeleteAllExactCopiesExceptAsync(
        DuplicateGroupItem group,
        DuplicateFileItem keeper,
        CancellationToken cancellationToken = default)
    {
        var candidates = group.Files.Where(x => x.Id != keeper.Id).ToList();
        return DeleteExactCandidatesPermanentlyAsync(group, keeper, candidates, cancellationToken);
    }

    public async Task<ExactDeleteBatchResult> DeleteExactCandidatesPermanentlyAsync(
        DuplicateGroupItem group,
        DuplicateFileItem keeper,
        IReadOnlyList<DuplicateFileItem> candidates,
        CancellationToken cancellationToken = default)
    {
        if (group.Files.Count < 2)
            throw new InvalidOperationException("В группе должно быть не меньше двух точных копий.");
        if (!group.Files.Any(x => x.Id == keeper.Id))
            throw new InvalidOperationException("Выбранный сохраняемый файл не относится к этой группе.");
        if (string.IsNullOrWhiteSpace(group.Sha256))
            throw new InvalidOperationException("У группы нет рассчитанного SHA-256.");

        var memberIds = group.Files.Select(x => x.Id).ToHashSet();
        var uniqueCandidates = candidates
            .Where(x => x.Id != keeper.Id)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
        if (uniqueCandidates.Any(x => !memberIds.Contains(x.Id)))
            throw new InvalidOperationException("Список удаления содержит файл из другой группы точных дублей.");

        // Irreversible mode gets a stronger keeper guarantee than quarantine: keep the verified
        // copy open with FileShare.Read for the whole group. On Windows this prevents another
        // process from replacing, renaming or deleting the keeper while candidates are removed.
        EnsureLibraryFilePathSafe(keeper.FullPath, keeper.SourceFolder);
        await using var keeperLock = await OpenVerifiedReadLockAsync(
            keeper.FullPath, keeper.FileSize, group.Sha256, cancellationToken);

        var result = new ExactDeleteBatchResult { Requested = uniqueCandidates.Count };
        foreach (var item in uniqueCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? tombstone = null;
            var catalogueCommitted = false;
            try
            {
                EnsureLibraryFilePathSafe(item.FullPath, item.SourceFolder);
                await VerifyFileAsync(item.FullPath, item.FileSize, group.Sha256, cancellationToken);

                var sourceDirectory = Path.GetDirectoryName(item.FullPath)
                    ?? throw new InvalidOperationException("У удаляемого файла отсутствует родительский каталог.");
                tombstone = Path.Combine(sourceDirectory, ".pam-delete-pending-" + Guid.NewGuid().ToString("N") + ".pamdel");
                if (File.Exists(tombstone))
                    throw new IOException("Временный путь окончательного удаления уже существует: " + tombstone);

                // Same-directory rename freezes the exact file we have just verified. The original
                // path disappears atomically, but until the catalogue commit succeeds we can roll back.
                File.Move(item.FullPath, tombstone);
                var frozenHash = await ComputeSha256Async(tombstone, cancellationToken);
                if (!string.Equals(frozenHash, group.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("SHA-256 временного файла изменился. Окончательное удаление отменено.");

                try
                {
                    await _database.RecordDirectExactDeletionAsync(
                        item.Id, item.FullPath, tombstone, group.Sha256, item.FileSize, CancellationToken.None);
                    catalogueCommitted = true;
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tombstone) && !File.Exists(item.FullPath))
                        {
                            var rollbackHash = await ComputeSha256Async(tombstone, CancellationToken.None);
                            if (string.Equals(rollbackHash, group.Sha256, StringComparison.OrdinalIgnoreCase))
                                File.Move(tombstone, item.FullPath);
                            else
                                LoggingService.Error("CRITICAL: direct-delete DB rollback refused to restore a tombstone whose SHA-256 changed: " + tombstone);
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        LoggingService.Error("CRITICAL: direct exact-delete DB rollback failed; tombstone=" + tombstone, rollbackEx);
                    }
                    throw;
                }

                // Database now contains an irreversible audit record. Recheck the frozen file one
                // final time and delete only that exact tombstone, never whatever may later appear
                // again at the original path.
                var finalHash = await ComputeSha256Async(tombstone, CancellationToken.None);
                if (!string.Equals(finalHash, group.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Файл изменился после записи журнала. PAM оставил временный файл на диске вместо удаления: " + tombstone);

                File.Delete(tombstone);
                if (File.Exists(tombstone))
                    throw new IOException("Windows не удалила временный файл окончательного удаления: " + tombstone);

                result.Succeeded++;
                result.BytesDeleted += item.FileSize;
            }
            catch (Exception ex)
            {
                if (!catalogueCommitted && !string.IsNullOrWhiteSpace(tombstone) && File.Exists(tombstone) && !File.Exists(item.FullPath))
                {
                    try
                    {
                        var rollbackHash = await ComputeSha256Async(tombstone, CancellationToken.None);
                        if (string.Equals(rollbackHash, group.Sha256, StringComparison.OrdinalIgnoreCase))
                            File.Move(tombstone, item.FullPath);
                        else
                            LoggingService.Error("CRITICAL: pre-commit direct-delete rollback refused to restore a tombstone whose SHA-256 changed: " + tombstone);
                    }
                    catch (Exception rollbackEx)
                    {
                        LoggingService.Error("CRITICAL: pre-commit direct exact-delete rollback failed; tombstone=" + tombstone, rollbackEx);
                    }
                }

                LoggingService.Error($"Direct exact-duplicate delete failed for {item.FullPath}", ex);
                var suffix = !string.IsNullOrWhiteSpace(tombstone) && File.Exists(tombstone)
                    ? catalogueCommitted
                        ? " (каталог уже отмечен как удалённый; временный файл сохранён: " + tombstone + ")"
                        : " (исходный путь не удалось восстановить; проверенный временный файл сохранён: " + tombstone + ")"
                    : "";
                result.Errors.Add($"{item.FullPath}: {ex.Message}{suffix}");
            }
        }

        return result;
    }


    public async Task<QuarantineBatchResult> QuarantineSelectedVisualAsync(
        VisualDuplicateGroupItem group,
        IReadOnlyList<VisualDuplicateFileItem> candidates,
        CancellationToken cancellationToken = default)
    {
        if (group.Files.Count < 2)
            throw new InvalidOperationException("В визуальной группе должно быть не меньше двух файлов.");
        if (candidates.Count == 0)
            throw new InvalidOperationException("Не отмечено ни одного файла для карантина.");

        var memberIds = group.Files.Select(x => x.Id).ToHashSet();
        if (candidates.Any(x => !memberIds.Contains(x.Id)))
            throw new InvalidOperationException("Список карантина содержит файл из другой визуальной группы.");
        var candidateIds = candidates.Select(x => x.Id).ToHashSet();
        if (candidateIds.Count != candidates.Count)
            throw new InvalidOperationException("Список карантина содержит один и тот же файл несколько раз.");
        var keepers = group.Files.Where(x => !candidateIds.Contains(x.Id)).ToList();
        if (keepers.Count == 0)
            throw new InvalidOperationException("Нельзя отправить в карантин всю визуальную группу: хотя бы один неотмеченный файл должен остаться на месте.");

        // Visual similarity is NOT an integrity guarantee. The only UI decision is now
        // "in quarantine": every unmarked file stays and is verified before any candidate
        // is moved; every marked candidate gets a fresh SHA-256 protecting the transfer.
        foreach (var keeper in keepers)
        {
            EnsureLibraryFilePathSafe(keeper.FullPath, keeper.SourceFolder);
            await VerifyIndexedSignatureAsync(keeper.FullPath, keeper.FileSize, keeper.LastWriteUtcTicks, cancellationToken);
        }

        var result = new QuarantineBatchResult { Requested = candidates.Count };
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureLibraryFilePathSafe(item.FullPath, item.SourceFolder);
                await VerifyIndexedSignatureAsync(item.FullPath, item.FileSize, item.LastWriteUtcTicks, cancellationToken);
                var freshSha256 = await ComputeSha256Async(item.FullPath, cancellationToken);
                var destination = BuildQuarantinePath(item.Id, item.FileName);
                var transfer = await TransferVerifiedAsync(item.FullPath, destination, freshSha256, cancellationToken);

                try
                {
                    await _database.RecordQuarantineAsync(
                        item.Id,
                        item.FullPath,
                        destination,
                        freshSha256,
                        item.FileSize,
                        transfer.SourceLastWriteUtc.Ticks,
                        transfer.SourceCreationUtc.Ticks,
                        CancellationToken.None);
                }
                catch
                {
                    try
                    {
                        await TransferVerifiedAsync(
                            destination, item.FullPath, freshSha256, CancellationToken.None,
                            transfer.SourceCreationUtc, transfer.SourceLastWriteUtc);
                    }
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
        IReadOnlyList<VisualDuplicateFileItem> candidates,
        CancellationToken cancellationToken = default)
    {
        if (group.Files.Count < 3)
            throw new InvalidOperationException("В серии должно быть не меньше трёх файлов.");
        if (candidates.Count == 0)
            throw new InvalidOperationException("Не отмечено ни одного кадра для карантина.");

        var memberIds = group.Files.Select(x => x.Id).ToHashSet();
        if (candidates.Any(x => !memberIds.Contains(x.Id)))
            throw new InvalidOperationException("Выбран файл, который не относится к этой серии.");
        var candidateIds = candidates.Select(x => x.Id).ToHashSet();
        if (candidateIds.Count != candidates.Count)
            throw new InvalidOperationException("Список карантина содержит один и тот же кадр несколько раз.");
        var keepers = group.Files.Where(x => !candidateIds.Contains(x.Id)).ToList();
        if (keepers.Count == 0)
            throw new InvalidOperationException("Нельзя отправить в карантин всю серию: хотя бы один неотмеченный кадр должен остаться на месте.");

        // A burst recommendation is only a culling aid, never an integrity guarantee.
        // Every unmarked frame stays and is verified first. Each marked frame then receives
        // a fresh SHA-256 that protects the quarantine transfer itself.
        foreach (var keeper in keepers)
        {
            EnsureLibraryFilePathSafe(keeper.FullPath, keeper.SourceFolder);
            await VerifyIndexedSignatureAsync(keeper.FullPath, keeper.FileSize, keeper.LastWriteUtcTicks, cancellationToken);
        }

        var result = new QuarantineBatchResult { Requested = candidates.Count };
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureLibraryFilePathSafe(item.FullPath, item.SourceFolder);
                await VerifyIndexedSignatureAsync(item.FullPath, item.FileSize, item.LastWriteUtcTicks, cancellationToken);
                var freshSha256 = await ComputeSha256Async(item.FullPath, cancellationToken);
                var destination = BuildQuarantinePath(item.Id, item.FileName);
                var transfer = await TransferVerifiedAsync(item.FullPath, destination, freshSha256, cancellationToken);

                try
                {
                    await _database.RecordQuarantineAsync(
                        item.Id,
                        item.FullPath,
                        destination,
                        freshSha256,
                        item.FileSize,
                        transfer.SourceLastWriteUtc.Ticks,
                        transfer.SourceCreationUtc.Ticks,
                        CancellationToken.None);
                }
                catch
                {
                    try
                    {
                        await TransferVerifiedAsync(
                            destination, item.FullPath, freshSha256, CancellationToken.None,
                            transfer.SourceCreationUtc, transfer.SourceLastWriteUtc);
                    }
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
        EnsureRestorePathInsideSource(action.OriginalPath, action.OriginalSourceFolder);
        EnsurePathInsideQuarantine(action.QuarantinePath);
        if (!File.Exists(action.QuarantinePath))
            throw new FileNotFoundException("Файл не найден в карантине.", action.QuarantinePath);

        await VerifyFileAsync(action.QuarantinePath, action.FileSize, action.Sha256, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(action.OriginalPath)!);

        var desiredCreationUtc = TryUtcFromTicks(action.OriginalCreationUtcTicks);
        var desiredLastWriteUtc = TryUtcFromTicks(action.OriginalLastWriteUtcTicks);
        var transfer = await TransferVerifiedAsync(
            action.QuarantinePath, action.OriginalPath, action.Sha256, cancellationToken,
            desiredCreationUtc, desiredLastWriteUtc);

        try
        {
            await _database.MarkQuarantineUndoneAsync(
                action.Id, action.FileId,
                transfer.DestinationLastWriteUtc.Ticks, transfer.DestinationCreationUtc.Ticks,
                CancellationToken.None);
        }
        catch
        {
            // Keep DB and filesystem consistent if the database update fails. Restore the dates
            // that the quarantine file actually had before Undo, not the desired original dates.
            try
            {
                await TransferVerifiedAsync(
                    action.OriginalPath, action.QuarantinePath, action.Sha256, CancellationToken.None,
                    transfer.SourceCreationUtc, transfer.SourceLastWriteUtc);
            }
            catch (Exception rollbackEx) { LoggingService.Error("Undo DB rollback failed", rollbackEx); }
            throw;
        }
    }

    public async Task DeletePermanentlyAsync(QuarantineActionItem action, CancellationToken cancellationToken = default)
    {
        if (!action.IsActive)
            throw new InvalidOperationException("Окончательно удалить можно только активный файл из карантина.");
        EnsurePathInsideQuarantine(action.QuarantinePath);
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
            await _database.MarkQuarantinePermanentlyDeletedAsync(action.Id, action.FileId, CancellationToken.None);
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
            var finalHash = await ComputeSha256Async(tombstone, CancellationToken.None);
            if (!string.Equals(finalHash, action.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Файл окончательного удаления изменился после записи журнала. PAM оставил его на диске вместо удаления.");
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



    private static string NormalizeDirectoryRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return !string.IsNullOrWhiteSpace(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void EnsureRestorePathInsideSource(string path, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sourceRoot))
            throw new InvalidOperationException("В журнале карантина отсутствует исходный путь или корень источника.");

        var rootFull = NormalizeDirectoryRoot(sourceRoot);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(rootFull, fullPath);
        if (string.IsNullOrWhiteSpace(relative) || relative == "." || Path.IsPathRooted(relative) ||
            relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Защитная проверка PAM отказалась восстанавливать файл вне его исходной папки библиотеки: " + fullPath);
        }

        RejectRestoreReparsePoint(rootFull);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = rootFull;
        for (var i = 0; i < Math.Max(0, parts.Length - 1); i++)
        {
            current = Path.Combine(current, parts[i]);
            RejectRestoreReparsePoint(current);
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            RejectRestoreReparsePoint(fullPath);
    }

    private static void RejectRestoreReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Защитная проверка PAM запрещает Undo карантина через junction/symlink: " + path);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException("Не удалось безопасно проверить путь восстановления карантина: " + path, ex);
        }
    }

    private static void EnsureLibraryFilePathSafe(string path, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sourceRoot))
            throw new InvalidOperationException("В каталоге PAM отсутствует путь файла или корень его источника.");

        var rootFull = NormalizeDirectoryRoot(sourceRoot);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(rootFull, fullPath);
        if (string.IsNullOrWhiteSpace(relative) || relative == "." || Path.IsPathRooted(relative) ||
            relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Защитная проверка PAM отказалась изменять файл вне записанной папки-источника: " + fullPath);
        }

        RejectLibraryReparsePoint(rootFull);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = rootFull;
        for (var i = 0; i < Math.Max(0, parts.Length - 1); i++)
        {
            current = Path.Combine(current, parts[i]);
            RejectLibraryReparsePoint(current);
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            RejectLibraryReparsePoint(fullPath);
    }

    private static void RejectLibraryReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Защитная проверка PAM запрещает физическую операцию с библиотекой через junction/symlink: " + path);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException("Не удалось безопасно проверить путь файла библиотеки: " + path, ex);
        }
    }

    private void EnsurePathInsideQuarantine(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("В журнале карантина отсутствует путь к файлу.");

        var rootFull = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(rootFull, fullPath);
        if (string.IsNullOrWhiteSpace(relative) || relative == "." || Path.IsPathRooted(relative) ||
            relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Защитная проверка PAM отказалась работать с файлом вне каталога карантина: " + fullPath);
        }

        // A lexical prefix check is not enough when a damaged/manually edited Data tree contains
        // a junction or symlink. Permanent deletion must never be able to escape Data\Quarantine
        // through a reparse point, even if SQLite contains a path that looks legitimate.
        RejectReparsePoint(rootFull, isDirectory: true);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = rootFull;
        for (var i = 0; i < Math.Max(0, parts.Length - 1); i++)
        {
            current = Path.Combine(current, parts[i]);
            RejectReparsePoint(current, isDirectory: true);
        }
        RejectReparsePoint(fullPath, isDirectory: false);
    }

    private static void RejectReparsePoint(string path, bool isDirectory)
    {
        var exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
        if (!exists) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Защитная проверка PAM запрещает работу через junction/symlink в карантине: " + path);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException("Не удалось безопасно проверить путь карантина: " + path, ex);
        }
    }

    private string BuildQuarantinePath(DuplicateFileItem item) => BuildQuarantinePath(item.Id, item.FileName);

    private string BuildQuarantinePath(long fileId, string fileName)
    {
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        var folder = Path.Combine(_root, day);

        var safeName = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        var unique = Guid.NewGuid().ToString("N")[..10];
        var destination = Path.Combine(folder, $"{fileId}_{stem}_{unique}{ext}");

        // Validate the existing root/intermediate path before creating the daily directory,
        // then validate again after creation. This avoids even creating a directory through
        // a pre-existing reparse point and narrows the race window before the physical move.
        EnsurePathInsideQuarantine(destination);
        Directory.CreateDirectory(folder);
        EnsurePathInsideQuarantine(destination);
        return destination;
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

    private static async Task<FileStream> OpenVerifiedReadLockAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        if (!File.Exists(path))
            throw new FileNotFoundException("Сохраняемая копия не найдена.", path);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedSize)
                throw new IOException($"Размер сохраняемой копии изменился: ожидалось {expectedSize}, сейчас {stream.Length} байт.");

            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, cancellationToken);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("SHA-256 сохраняемой копии изменился после анализа. Удаление отменено.");

            stream.Position = 0;
            return stream;
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync();
            throw;
        }
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

    private static async Task<VerifiedTransferResult> TransferVerifiedAsync(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken,
        DateTime? desiredCreationUtc = null,
        DateTime? desiredLastWriteUtc = null)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("Исходный файл не найден.", source);
        if (File.Exists(destination))
            throw new IOException("Целевой файл уже существует: " + destination);

        var sourceInfo = new FileInfo(source);
        var sourceCreationUtc = sourceInfo.CreationTimeUtc;
        var sourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
        var targetCreationUtc = desiredCreationUtc ?? sourceCreationUtc;
        var targetLastWriteUtc = desiredLastWriteUtc ?? sourceLastWriteUtc;

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
                // Verify the FINAL path. Matching full-file SHA-256 proves embedded EXIF/IPTC/XMP
                // metadata is preserved byte-for-byte. Quarantine also preserves filesystem dates.
                await VerifyFinalTransferredFileAsync(
                    destination, expectedSha256, targetCreationUtc, targetLastWriteUtc, cancellationToken);
            }
            catch
            {
                try
                {
                    if (!File.Exists(source) && File.Exists(destination))
                    {
                        var rollbackHash = await ComputeSha256Async(destination, CancellationToken.None);
                        if (string.Equals(rollbackHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Move(destination, source);
                            ApplyAndVerifyFileDates(source, sourceCreationUtc, sourceLastWriteUtc);
                        }
                        else
                            LoggingService.Error("CRITICAL: same-volume rollback refused to move a destination whose SHA-256 no longer matches the transferred file.");
                    }
                }
                catch (Exception rollbackEx)
                {
                    LoggingService.Error("Same-volume transfer rollback failed", rollbackEx);
                }
                throw;
            }
            var sameVolumeInfo = new FileInfo(destination);
            return new VerifiedTransferResult(
                sourceCreationUtc, sourceLastWriteUtc,
                sameVolumeInfo.CreationTimeUtc, sameVolumeInfo.LastWriteTimeUtc);
        }

        // Cross-volume moves are copy -> freeze exact source by same-volume rename -> verify source
        // tombstone -> place copy at the FINAL path -> verify that final SHA-256 and dates -> delete
        // only the verified source tombstone. This closes replacement and copy-corruption races.
        var temp = destination + ".pamtmp-" + Guid.NewGuid().ToString("N");
        var sourceDirectory = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("У исходного файла отсутствует родительский каталог.");
        var sourceTombstone = Path.Combine(
            sourceDirectory,
            ".pam-source-pending-" + Guid.NewGuid().ToString("N") + ".pamtmp");
        var sourceTombstoned = false;
        var destinationCreated = false;
        try
        {
            await CopyFileAsync(source, temp, cancellationToken);

            File.Move(source, sourceTombstone);
            sourceTombstoned = true;
            var tombstoneHash = await ComputeSha256Async(sourceTombstone, cancellationToken);
            if (!string.Equals(tombstoneHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Исходный файл изменился во время переноса. Удаление отменено.");

            if (File.Exists(destination))
                throw new IOException("Целевой файл появился во время копирования. Перезапись запрещена: " + destination);
            File.Move(temp, destination);
            destinationCreated = true;

            // The source is not deleted until the actual final quarantine file has passed both
            // full SHA-256 and filesystem-date verification.
            await VerifyFinalTransferredFileAsync(
                destination, expectedSha256, targetCreationUtc, targetLastWriteUtc, cancellationToken);

            var finalTombstoneHash = await ComputeSha256Async(sourceTombstone, CancellationToken.None);
            if (!string.Equals(finalTombstoneHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Исходный tombstone изменился непосредственно перед удалением. Автоматическое удаление отменено.");
            File.Delete(sourceTombstone);
            if (File.Exists(sourceTombstone))
                throw new IOException("Windows не удалила проверенный исходный tombstone.");
            sourceTombstoned = false;
        }
        catch
        {
            try
            {
                if (destinationCreated && File.Exists(destination))
                {
                    var destinationHash = await ComputeSha256Async(destination, CancellationToken.None);
                    if (string.Equals(destinationHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(destination);
                        destinationCreated = false;
                    }
                    else
                    {
                        LoggingService.Error("CRITICAL: rollback refused to delete a destination whose SHA-256 no longer matches the file created by PAM: " + destination);
                    }
                }
                if (sourceTombstoned && File.Exists(sourceTombstone) && !File.Exists(source))
                {
                    var rollbackSourceHash = await ComputeSha256Async(sourceTombstone, CancellationToken.None);
                    if (string.Equals(rollbackSourceHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(sourceTombstone, source);
                        ApplyAndVerifyFileDates(source, sourceCreationUtc, sourceLastWriteUtc);
                        sourceTombstoned = false;
                    }
                    else
                    {
                        LoggingService.Error("CRITICAL: rollback refused to move a source tombstone whose SHA-256 changed: " + sourceTombstone);
                    }
                }
            }
            catch (Exception rollbackEx)
            {
                LoggingService.Error("CRITICAL: cross-volume quarantine rollback failed; source tombstone=" + sourceTombstone, rollbackEx);
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* best effort temp cleanup */ }
        }

        var crossVolumeInfo = new FileInfo(destination);
        return new VerifiedTransferResult(
            sourceCreationUtc, sourceLastWriteUtc,
            crossVolumeInfo.CreationTimeUtc, crossVolumeInfo.LastWriteTimeUtc);
    }

    private readonly record struct VerifiedTransferResult(
        DateTime SourceCreationUtc,
        DateTime SourceLastWriteUtc,
        DateTime DestinationCreationUtc,
        DateTime DestinationLastWriteUtc);

    private static DateTime? TryUtcFromTicks(long ticks)
    {
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
        try { return new DateTime(ticks, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static async Task VerifyFinalTransferredFileAsync(
        string path,
        string expectedSha256,
        DateTime desiredCreationUtc,
        DateTime desiredLastWriteUtc,
        CancellationToken cancellationToken)
    {
        var finalHash = await ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(finalHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Проверка SHA-256 конечного файла после переноса не пройдена. Исходник не будет удалён.");

        ApplyAndVerifyFileDates(path, desiredCreationUtc, desiredLastWriteUtc);
    }

    private static void ApplyAndVerifyFileDates(string path, DateTime desiredCreationUtc, DateTime desiredLastWriteUtc)
    {
        File.SetCreationTimeUtc(path, desiredCreationUtc);
        File.SetLastWriteTimeUtc(path, desiredLastWriteUtc);

        var info = new FileInfo(path);
        VerifyFileDate("дата создания", info.CreationTimeUtc, desiredCreationUtc, path);
        VerifyFileDate("дата изменения", info.LastWriteTimeUtc, desiredLastWriteUtc, path);
    }

    private static void VerifyFileDate(string label, DateTime actualUtc, DateTime desiredUtc, string path)
    {
        var delta = (actualUtc - desiredUtc).Duration();
        if (delta > TimeSpan.FromSeconds(2))
            throw new IOException($"Не удалось сохранить {label} файла после переноса: {path}");
        if (delta > TimeSpan.Zero)
            LoggingService.Warn($"Файловая система округлила {label} при переносе на {delta.TotalMilliseconds:0} мс: {path}");
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
