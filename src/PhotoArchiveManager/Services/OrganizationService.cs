using System.Security.Cryptography;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class OrganizationService
{
    private readonly DatabaseService _database;
    private readonly AsyncPauseGate _pauseGate = new();
    private CancellationTokenSource? _runCts;

    public OrganizationService(DatabaseService database)
    {
        _database = database;
    }

    public bool IsPaused => _pauseGate.IsPaused;
    public void Pause() => _pauseGate.Pause();
    public void Resume() => _pauseGate.Resume();
    public void Stop() => _runCts?.Cancel();

    public async Task<List<OrganizationPlanItem>> BuildPlanAsync(
        string destinationRoot,
        bool useFallbackDates,
        bool includeDateInFileName,
        bool includeNamedPeopleInFileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentException("Сначала выберите корневую папку нового архива.");

        destinationRoot = NormalizeDirectory(destinationRoot);
        ValidateDestination(destinationRoot, await _database.GetSourceFoldersAsync());

        var candidates = await _database.GetOrganizationCandidatesAsync(cancellationToken);
        var occupiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<OrganizationPlanItem>(candidates.Count);

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(item.FullPath);
            var trustedDate = CaptureDatePolicy.IsTrusted(item.CaptureDateSource);
            var parsedDate = DateTime.TryParse(item.CaptureDate, out var captureDate);
            var canUseDate = parsedDate && (trustedDate || useFallbackDates);

            string relativeFolder;
            string dateDisplay;
            string eventDisplay = "";
            var status = "Готово";
            var details = "";
            var ready = true;

            if (!canUseDate)
            {
                relativeFolder = "Без достоверной даты";
                dateDisplay = parsedDate
                    ? $"{captureDate:dd.MM.yyyy HH:mm} · {item.CaptureDateSource}"
                    : "Дата неизвестна";
                status = "Нужна проверка";
                details = trustedDate ? "Не удалось разобрать доверенную дату." : "Нет достоверной даты (EXIF или ручной); по умолчанию файл не выбран для перемещения.";
            }
            else
            {
                dateDisplay = $"{captureDate:dd.MM.yyyy HH:mm} · {item.CaptureDateSource}";
                var eventStart = DateTime.TryParse(item.EventStartDate, out var es) ? es : captureDate;
                var eventEnd = DateTime.TryParse(item.EventEndDate, out var ee) ? ee : eventStart;
                if (eventEnd < eventStart) eventEnd = eventStart;
                var eventNameIsOnlyLegacyDate = IsLegacyDateOnlyEventName(item.EventName, eventStart, eventEnd);
                var eventNameAllowed = !string.IsNullOrWhiteSpace(item.EventName)
                                       && !item.EventIsAuto
                                       && !eventNameIsOnlyLegacyDate;
                if (eventNameAllowed)
                {
                    var monthPart = BuildMonthRange(eventStart.Date, eventEnd.Date);
                    var safeEvent = SanitizeSegment(item.EventName, 90);
                    relativeFolder = Path.Combine(eventStart.Year.ToString("0000"), $"{monthPart} — {safeEvent}");
                    eventDisplay = item.EventName;
                }
                else
                {
                    relativeFolder = Path.Combine(captureDate.Year.ToString("0000"), GetMonthAbbreviation(captureDate.Month));
                    if (!string.IsNullOrWhiteSpace(item.EventName))
                    {
                        eventDisplay = eventNameIsOnlyLegacyDate
                            ? "Служебное название по дате не используется в папке: " + item.EventName
                            : item.EventIsAuto ? "Автособытие не использовано: " + item.EventName : item.EventName;
                    }
                }
            }

            var targetFolder = Path.Combine(destinationRoot, relativeFolder);
            var originalName = SanitizeFileName(string.IsNullOrWhiteSpace(item.OriginalFileName) ? item.FileName : item.OriginalFileName);
            var targetFileName = BuildTargetFileName(
                originalName,
                canUseDate ? captureDate : null,
                item.NamedPeople,
                includeDateInFileName,
                includeNamedPeopleInFileName);
            var targetPath = Path.Combine(targetFolder, targetFileName);
            var autoRenamed = false;

            if (PathsEqual(sourcePath, targetPath))
            {
                plan.Add(new OrganizationPlanItem
                {
                    FileId = item.Id,
                    SourcePath = sourcePath,
                    SourceFolder = item.SourceFolder,
                    TargetPath = targetPath,
                    DestinationRoot = destinationRoot,
                    FileName = item.FileName,
                    TargetFileName = targetFileName,
                    ThumbnailPath = item.ThumbnailPath,
                    FileSize = item.FileSize,
                    LastWriteUtcTicks = item.LastWriteUtcTicks,
                    CaptureDateSource = item.CaptureDateSource,
                    DateDisplay = dateDisplay,
                    EventDisplay = eventDisplay,
                    Status = "Уже на месте",
                    StatusDetails = "Файл уже лежит точно по предлагаемому пути.",
                    HasTrustedDate = trustedDate,
                    IsReady = false,
                    IsAlreadyCorrect = true,
                    IsSelected = false,
                    CachedSha256 = item.Sha256,
                    HasValidCachedSha256 = IsValidCachedHash(item)
                });
                occupiedTargets.Add(targetPath);
                continue;
            }

            if (File.Exists(targetPath) || occupiedTargets.Contains(targetPath))
            {
                targetPath = BuildUniqueTarget(targetFolder, targetFileName, occupiedTargets);
                autoRenamed = true;
                details = Append(details, "Имя уже было занято; PAM безопасно добавил суффикс к имени. Ничего не будет перезаписано.");
            }

            if (targetPath.Length > 245)
            {
                targetPath = ShortenTargetPath(targetFolder, targetFileName, occupiedTargets);
                autoRenamed = true;
                details = Append(details, "Имя укорочено, чтобы избежать слишком длинного пути Windows.");
            }

            if (targetPath.Length > 245)
            {
                status = "Конфликт";
                details = Append(details, "Даже после сокращения имя целевого пути слишком длинное. Выберите более короткий корень архива.");
                ready = false;
            }

            if (File.Exists(targetPath) || occupiedTargets.Contains(targetPath))
            {
                status = "Конфликт";
                details = Append(details, "Не удалось подобрать свободный целевой путь.");
                ready = false;
            }

            occupiedTargets.Add(targetPath);
            var row = new OrganizationPlanItem
            {
                FileId = item.Id,
                SourcePath = sourcePath,
                SourceFolder = item.SourceFolder,
                TargetPath = targetPath,
                DestinationRoot = destinationRoot,
                FileName = item.FileName,
                TargetFileName = Path.GetFileName(targetPath),
                ThumbnailPath = item.ThumbnailPath,
                FileSize = item.FileSize,
                LastWriteUtcTicks = item.LastWriteUtcTicks,
                CaptureDateSource = item.CaptureDateSource,
                DateDisplay = dateDisplay,
                EventDisplay = eventDisplay,
                Status = status,
                StatusDetails = details,
                HasTrustedDate = trustedDate,
                IsReady = ready,
                IsAlreadyCorrect = false,
                WasAutoRenamed = autoRenamed,
                CachedSha256 = item.Sha256,
                HasValidCachedSha256 = IsValidCachedHash(item)
            };
            row.IsSelected = ready && (trustedDate || useFallbackDates);
            plan.Add(row);
        }

        return plan;
    }

    public async Task<OrganizationBatchResult> ExecuteAsync(
        IReadOnlyList<OrganizationPlanItem> selected,
        IProgress<OrganizationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (selected.Count == 0)
            throw new InvalidOperationException("В плане не выбрано ни одного файла для перемещения.");
        if (selected.Any(x => !x.IsReady))
            throw new InvalidOperationException("В выбранных строках есть элементы, которые нельзя безопасно выполнить.");

        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _runCts.Token;
        var result = new OrganizationBatchResult { Requested = selected.Count };
        var processed = 0;

        try
        {
            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                await _pauseGate.WaitIfPausedAsync(ct);
                progress?.Report(new OrganizationProgress("Проверка", selected.Count, processed, result.Succeeded, result.Failed, result.BytesMoved, item.SourcePath));

                try
                {
                    await VerifyIndexedSignatureAsync(item.SourcePath, item.FileSize, item.LastWriteUtcTicks, ct);
                    if (File.Exists(item.TargetPath))
                        throw new IOException("Целевой файл появился после построения плана. Перезапись запрещена: " + item.TargetPath);

                    var sha256 = item.HasValidCachedSha256 && !string.IsNullOrWhiteSpace(item.CachedSha256)
                        ? item.CachedSha256
                        : await ComputeSha256Async(item.SourcePath, ct);

                    var originalLastWrite = File.GetLastWriteTimeUtc(item.SourcePath);
                    await TransferVerifiedAsync(item.SourcePath, item.TargetPath, sha256, originalLastWrite, ct);

                    try
                    {
                        await _database.RecordOrganizationMoveAsync(
                            item.FileId,
                            item.SourcePath,
                            item.TargetPath,
                            item.SourceFolder,
                            item.DestinationRoot,
                            sha256,
                            item.FileSize,
                            ct);
                    }
                    catch
                    {
                        try { await TransferVerifiedAsync(item.TargetPath, item.SourcePath, sha256, originalLastWrite, ct); }
                        catch (Exception rollbackEx) { LoggingService.Error("CRITICAL: organization DB rollback failed", rollbackEx); }
                        throw;
                    }

                    result.Succeeded++;
                    result.BytesMoved += item.FileSize;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.Errors.Add(item.SourcePath + ": " + ex.Message);
                    LoggingService.Error("Organization move failed for " + item.SourcePath, ex);
                }

                processed++;
                progress?.Report(new OrganizationProgress("Перемещение", selected.Count, processed, result.Succeeded, result.Failed, result.BytesMoved, item.SourcePath));
            }
        }
        finally
        {
            _pauseGate.Resume();
            _runCts.Dispose();
            _runCts = null;
        }

        return result;
    }

    public async Task UndoAsync(OrganizationActionItem action, CancellationToken cancellationToken = default)
    {
        if (!action.IsActive)
            throw new InvalidOperationException("Эта операция организации уже отменена.");
        if (!File.Exists(action.NewPath))
            throw new FileNotFoundException("Перемещённый файл не найден по новому пути.", action.NewPath);
        if (File.Exists(action.OriginalPath))
            throw new IOException("По исходному пути уже существует файл. PAM ничего не будет перезаписывать: " + action.OriginalPath);

        await VerifyFileAsync(action.NewPath, action.FileSize, action.Sha256, cancellationToken);
        var lastWrite = File.GetLastWriteTimeUtc(action.NewPath);
        await TransferVerifiedAsync(action.NewPath, action.OriginalPath, action.Sha256, lastWrite, cancellationToken);

        try
        {
            await _database.MarkOrganizationMoveUndoneAsync(action.Id, action.FileId, cancellationToken);
        }
        catch
        {
            try { await TransferVerifiedAsync(action.OriginalPath, action.NewPath, action.Sha256, lastWrite, cancellationToken); }
            catch (Exception rollbackEx) { LoggingService.Error("CRITICAL: organization Undo DB rollback failed", rollbackEx); }
            throw;
        }
    }

    private static void ValidateDestination(string destinationRoot, IReadOnlyList<SourceFolderItem> sources)
    {
        foreach (var source in sources)
        {
            var sourcePath = NormalizeDirectory(source.Path);
            if (PathsEqual(sourcePath, destinationRoot))
                continue; // Allows resuming or deliberately reorganizing within exactly the same root.

            if (IsInside(destinationRoot, sourcePath) || IsInside(sourcePath, destinationRoot))
                throw new InvalidOperationException(
                    "Папка назначения не должна быть вложена в существующий источник и не должна содержать источник внутри себя. " +
                    "Это защищает от повторного сканирования и циклической раскладки. Конфликтующий источник: " + source.Path);
        }
    }

    private static bool IsInside(string path, string possibleParent)
    {
        var relative = Path.GetRelativePath(possibleParent, path);
        return relative != "." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != ".." && !Path.IsPathRooted(relative);
    }

    private static bool IsValidCachedHash(OrganizationCandidate item) =>
        !string.IsNullOrWhiteSpace(item.Sha256) && item.HashFileSize == item.FileSize && item.HashLastWriteUtcTicks == item.LastWriteUtcTicks;

    private static readonly string[] RussianMonthAbbreviations =
    [
        "янв", "февр", "мар", "апр", "май", "июн",
        "июл", "авг", "сент", "окт", "нояб", "дек"
    ];

    private static string GetMonthAbbreviation(int month) =>
        month is >= 1 and <= 12 ? RussianMonthAbbreviations[month - 1] : "месяц";

    private static string BuildMonthRange(DateTime start, DateTime end)
    {
        var first = GetMonthAbbreviation(start.Month);
        var last = GetMonthAbbreviation(end.Month);
        return start.Year == end.Year && start.Month == end.Month ? first : $"{first}-{last}";
    }

    private static bool IsLegacyDateOnlyEventName(string name, DateTime start, DateTime end)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string generated;
        if (start.Date == end.Date)
            generated = $"{start:dd.MM.yyyy} · {start:HH:mm}–{end:HH:mm}";
        else if (start.Year == end.Year)
            generated = $"{start:dd.MM}–{end:dd.MM.yyyy}";
        else
            generated = $"{start:dd.MM.yyyy}–{end:dd.MM.yyyy}";
        return string.Equals(name.Trim(), generated, StringComparison.Ordinal);
    }

    private static string BuildTargetFileName(
        string originalFileName,
        DateTime? captureDate,
        IReadOnlyList<string> namedPeople,
        bool includeDate,
        bool includePeople)
    {
        var safeOriginal = SanitizeFileName(originalFileName);
        var stem = Path.GetFileNameWithoutExtension(safeOriginal);
        var extension = Path.GetExtension(safeOriginal);

        var prefix = includeDate && captureDate.HasValue
            ? captureDate.Value.ToString("yyyy-MM-dd") + "_"
            : "";

        var peopleSuffix = "";
        if (includePeople && namedPeople.Count > 0)
        {
            var people = namedPeople
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => SanitizeNameFragment(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            if (people.Length > 0) peopleSuffix = " — " + string.Join(", ", people);
        }

        return SanitizeFileName(prefix + stem + peopleSuffix + extension);
    }

    private static string SanitizeNameFragment(string value)
    {
        var cleaned = value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return cleaned.Trim(' ', '.');
    }

    private static string BuildUniqueTarget(string folder, string fileName, HashSet<string> occupied)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i <= 9999; i++)
        {
            var candidate = Path.Combine(folder, $"{stem}__{i}{ext}");
            if (!File.Exists(candidate) && !occupied.Contains(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem}__{Guid.NewGuid():N}{ext}");
    }

    private static string ShortenTargetPath(string folder, string fileName, HashSet<string> occupied)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var room = Math.Max(16, 240 - folder.Length - ext.Length - 2);
        if (stem.Length > room) stem = stem[..room];
        var candidate = Path.Combine(folder, stem + ext);
        if (!File.Exists(candidate) && !occupied.Contains(candidate)) return candidate;
        return BuildUniqueTarget(folder, stem + ext, occupied);
    }

    private static string SanitizeSegment(string value, int maxLength)
    {
        var cleaned = value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        cleaned = cleaned.Trim(' ', '.');
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength].Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Событие";
        if (IsReservedWindowsName(cleaned)) cleaned = "_" + cleaned;
        return cleaned;
    }

    private static string SanitizeFileName(string value)
    {
        var fileName = Path.GetFileName(value);
        foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
        fileName = fileName.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "photo";
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (IsReservedWindowsName(stem)) fileName = "_" + fileName;
        return fileName;
    }

    private static bool IsReservedWindowsName(string name)
    {
        var upper = name.TrimEnd('.').ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL" ||
               (upper.StartsWith("COM") && int.TryParse(upper[3..], out var com) && com is >= 1 and <= 9) ||
               (upper.StartsWith("LPT") && int.TryParse(upper[3..], out var lpt) && lpt is >= 1 and <= 9);
    }

    private static string Append(string current, string next) => string.IsNullOrWhiteSpace(current) ? next : current + " " + next;

    private static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    private static bool PathsEqual(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static Task VerifyIndexedSignatureAsync(string path, long expectedSize, long expectedLastWriteUtcTicks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        var info = new FileInfo(path);
        if (info.Length != expectedSize || info.LastWriteTimeUtc.Ticks != expectedLastWriteUtcTicks)
            throw new IOException("Файл изменился после индексации. Пересканируйте библиотеку и заново постройте план.");
        return Task.CompletedTask;
    }

    private static async Task VerifyFileAsync(string path, long expectedSize, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.", path);
        if (new FileInfo(path).Length != expectedSize) throw new IOException("Размер файла изменился.");
        var actual = await ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("SHA-256 файла не совпадает с журналом. Операция отменена.");
    }

    private static async Task TransferVerifiedAsync(string source, string destination, string expectedSha256, DateTime sourceLastWriteUtc, CancellationToken cancellationToken)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Исходный файл не найден.", source);
        if (File.Exists(destination)) throw new IOException("Целевой файл уже существует. Перезапись запрещена: " + destination);
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
                    throw new IOException("Контрольный SHA-256 после перемещения не совпал.");
            }
            catch
            {
                try
                {
                    if (File.Exists(destination) && !File.Exists(source)) File.Move(destination, source);
                }
                catch (Exception rollbackEx) { LoggingService.Error("CRITICAL: same-volume move rollback failed", rollbackEx); }
                throw;
            }
            return;
        }

        var temp = destination + ".pamtmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await CopyFileAsync(source, temp, cancellationToken);
            File.SetLastWriteTimeUtc(temp, sourceLastWriteUtc);
            var copiedHash = await ComputeSha256Async(temp, cancellationToken);
            if (!string.Equals(copiedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Контрольный SHA-256 скопированного файла не совпал.");
            if (File.Exists(destination)) throw new IOException("Целевой файл появился во время копирования. Перезапись запрещена.");
            File.Move(temp, destination);
            try
            {
                File.Delete(source);
                if (File.Exists(source)) throw new IOException("После проверенного копирования Windows не удалила исходный файл.");
            }
            catch
            {
                // At this point source still exists, so the safest rollback is to remove only the verified copy.
                try { if (File.Exists(source) && File.Exists(destination)) File.Delete(destination); }
                catch (Exception rollbackEx) { LoggingService.Error("CRITICAL: cross-volume copy rollback failed", rollbackEx); }
                throw;
            }
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, bufferSize, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
