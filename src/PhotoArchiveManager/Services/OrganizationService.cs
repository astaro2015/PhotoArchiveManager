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
        OrganizationLayoutMode layoutMode,
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
            var parsedDate = StoredDateTime.TryParse(item.CaptureDate, out var captureDate);
            var hasUsableTrustedDate = trustedDate && parsedDate;
            var canUseDate = parsedDate && (trustedDate || useFallbackDates);
            // Use the actual embedded EXIF date for filesystem CreationTime even when the user
            // has overridden the catalog CaptureDate manually. AutoCaptureDate retains the scanner's
            // metadata-derived value; a file-date fallback is deliberately not treated as embedded EXIF.
            DateTime? embeddedCaptureDate = null;
            if (item.CaptureDateSource.StartsWith("EXIF", StringComparison.OrdinalIgnoreCase) && parsedDate)
                embeddedCaptureDate = captureDate;
            else if (item.AutoCaptureDateSource.StartsWith("EXIF", StringComparison.OrdinalIgnoreCase) &&
                     StoredDateTime.TryParse(item.AutoCaptureDate, out var autoCaptureDate))
                embeddedCaptureDate = autoCaptureDate;

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
                relativeFolder = layoutMode switch
                {
                    OrganizationLayoutMode.Year => captureDate.Year.ToString("0000"),
                    OrganizationLayoutMode.YearMonth => Path.Combine(
                        captureDate.Year.ToString("0000"),
                        BuildQuickMonthFolder(captureDate.Month)),
                    OrganizationLayoutMode.YearMonthDay => Path.Combine(
                        captureDate.Year.ToString("0000"),
                        BuildQuickMonthFolder(captureDate.Month),
                        captureDate.Day.ToString("00")),
                    _ => BuildFullRelativeFolder(item, captureDate, out eventDisplay)
                };
            }

            var targetFolder = Path.Combine(destinationRoot, relativeFolder);
            var originalName = SanitizeFileName(string.IsNullOrWhiteSpace(item.OriginalFileName) ? item.FileName : item.OriginalFileName);
            var isFullLayout = layoutMode == OrganizationLayoutMode.Full;
            var targetFileName = BuildTargetFileName(
                originalName,
                canUseDate ? captureDate : null,
                item.NamedPeople,
                isFullLayout && includeDateInFileName,
                isFullLayout && includeNamedPeopleInFileName);
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
                    EmbeddedCaptureDate = embeddedCaptureDate,
                    DateDisplay = dateDisplay,
                    EventDisplay = eventDisplay,
                    Status = "Уже на месте",
                    StatusDetails = "Файл уже лежит точно по предлагаемому пути.",
                    HasTrustedDate = hasUsableTrustedDate,
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
                EmbeddedCaptureDate = embeddedCaptureDate,
                DateDisplay = dateDisplay,
                EventDisplay = eventDisplay,
                Status = status,
                StatusDetails = details,
                HasTrustedDate = hasUsableTrustedDate,
                IsReady = ready,
                IsAlreadyCorrect = false,
                WasAutoRenamed = autoRenamed,
                CachedSha256 = item.Sha256,
                HasValidCachedSha256 = IsValidCachedHash(item)
            };
            row.IsSelected = ready && canUseDate;
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
                    EnsureFileInsideRoot(item.SourcePath, item.SourceFolder, "исходный");
                    EnsureFileInsideRoot(item.TargetPath, item.DestinationRoot, "целевой");
                    EnsureNoReparsePoints(item.SourcePath, item.SourceFolder, "исходный");
                    EnsureNoReparsePoints(item.TargetPath, item.DestinationRoot, "целевой");
                    await VerifyIndexedSignatureAsync(item.SourcePath, item.FileSize, item.LastWriteUtcTicks, ct);
                    if (File.Exists(item.TargetPath))
                        throw new IOException("Целевой файл появился после построения плана. Перезапись запрещена: " + item.TargetPath);

                    var sha256 = item.HasValidCachedSha256 && !string.IsNullOrWhiteSpace(item.CachedSha256)
                        ? item.CachedSha256
                        : await ComputeSha256Async(item.SourcePath, ct);

                    var originalInfo = new FileInfo(item.SourcePath);
                    var originalCreationUtc = originalInfo.CreationTimeUtc;
                    var originalLastWriteUtc = originalInfo.LastWriteTimeUtc;
                    var destinationCreationUtc = ResolveDestinationCreationUtc(
                        item.EmbeddedCaptureDate, originalCreationUtc, item.SourcePath);

                    await TransferVerifiedAsync(
                        item.SourcePath, item.TargetPath, sha256,
                        destinationCreationUtc, originalLastWriteUtc, ct);

                    var movedInfo = new FileInfo(item.TargetPath);
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
                            originalLastWriteUtc.Ticks,
                            originalCreationUtc.Ticks,
                            movedInfo.LastWriteTimeUtc.Ticks,
                            movedInfo.CreationTimeUtc.Ticks,
                            CancellationToken.None);
                    }
                    catch
                    {
                        try
                        {
                            await TransferVerifiedAsync(
                                item.TargetPath, item.SourcePath, sha256,
                                originalCreationUtc, originalLastWriteUtc, CancellationToken.None);
                        }
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

        // Journal paths are treated as untrusted input. Even a damaged/manually edited SQLite row
        // must not make Undo move an arbitrary file outside the roots recorded by the original plan.
        EnsureFileInsideRoot(action.NewPath, action.NewSourceFolder, "текущий");
        EnsureFileInsideRoot(action.OriginalPath, action.OriginalSourceFolder, "исходный");
        EnsureNoReparsePoints(action.NewPath, action.NewSourceFolder, "текущий");
        EnsureNoReparsePoints(action.OriginalPath, action.OriginalSourceFolder, "исходный");

        if (!File.Exists(action.NewPath))
            throw new FileNotFoundException("Перемещённый файл не найден по новому пути.", action.NewPath);
        if (File.Exists(action.OriginalPath))
            throw new IOException("По исходному пути уже существует файл. PAM ничего не будет перезаписывать: " + action.OriginalPath);

        await VerifyFileAsync(action.NewPath, action.FileSize, action.Sha256, cancellationToken);
        var movedInfo = new FileInfo(action.NewPath);
        var movedCreationUtc = movedInfo.CreationTimeUtc;
        var movedLastWriteUtc = movedInfo.LastWriteTimeUtc;
        var restoreCreationUtc = TryDateTimeFromTicks(action.OriginalCreationUtcTicks, out var originalCreationUtc)
            ? originalCreationUtc
            : movedCreationUtc;
        var restoreLastWriteUtc = TryDateTimeFromTicks(action.OriginalLastWriteUtcTicks, out var originalLastWriteUtc)
            ? originalLastWriteUtc
            : movedLastWriteUtc;
        await TransferVerifiedAsync(
            action.NewPath, action.OriginalPath, action.Sha256,
            restoreCreationUtc, restoreLastWriteUtc, cancellationToken);

        var restoredInfo = new FileInfo(action.OriginalPath);
        try
        {
            await _database.MarkOrganizationMoveUndoneAsync(
                action.Id, action.FileId,
                restoredInfo.LastWriteTimeUtc.Ticks, restoredInfo.CreationTimeUtc.Ticks,
                CancellationToken.None);
        }
        catch
        {
            try
            {
                await TransferVerifiedAsync(
                    action.OriginalPath, action.NewPath, action.Sha256,
                    movedCreationUtc, movedLastWriteUtc, CancellationToken.None);
            }
            catch (Exception rollbackEx) { LoggingService.Error("CRITICAL: organization Undo DB rollback failed", rollbackEx); }
            throw;
        }
    }

    private static void EnsureFileInsideRoot(string path, string root, string role)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"Защитная проверка пути организации не пройдена ({role} путь/корень пуст).");

        var fullRoot = NormalizeDirectory(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Защитная проверка PAM отказалась перемещать {role} файл вне записанного корня организации: {fullPath}");
        }
    }

    private static void EnsureNoReparsePoints(string path, string root, string role)
    {
        var fullRoot = NormalizeDirectory(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);

        RejectOrganizationReparsePoint(fullRoot, role);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = fullRoot;
        // Check every existing directory component. The final file itself is checked too when it
        // already exists (source/Undo); a not-yet-created destination file is naturally skipped.
        for (var i = 0; i < Math.Max(0, parts.Length - 1); i++)
        {
            current = Path.Combine(current, parts[i]);
            RejectOrganizationReparsePoint(current, role);
        }
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            RejectOrganizationReparsePoint(fullPath, role);
    }

    private static void RejectOrganizationReparsePoint(string path, string role)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"Защитная проверка PAM запрещает физическую организацию через junction/symlink ({role}): {path}");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException($"Не удалось безопасно проверить {role} путь организации: {path}", ex);
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

    private static string BuildFullRelativeFolder(OrganizationCandidate item, DateTime captureDate, out string eventDisplay)
    {
        eventDisplay = "";
        var eventStart = StoredDateTime.TryParse(item.EventStartDate, out var es) ? es : captureDate;
        var eventEnd = StoredDateTime.TryParse(item.EventEndDate, out var ee) ? ee : eventStart;
        if (eventEnd < eventStart) eventEnd = eventStart;
        var eventNameIsOnlyLegacyDate = IsLegacyDateOnlyEventName(item.EventName, eventStart, eventEnd);
        var eventNameAllowed = !string.IsNullOrWhiteSpace(item.EventName)
                               && !item.EventIsAuto
                               && !eventNameIsOnlyLegacyDate;
        if (eventNameAllowed)
        {
            var monthPart = BuildMonthRange(eventStart.Date, eventEnd.Date);
            var safeEvent = SanitizeSegment(item.EventName, 90);
            eventDisplay = item.EventName;
            return Path.Combine(eventStart.Year.ToString("0000"), $"{monthPart} — {safeEvent}");
        }

        if (!string.IsNullOrWhiteSpace(item.EventName))
        {
            eventDisplay = eventNameIsOnlyLegacyDate
                ? "Служебное название по дате не используется в папке: " + item.EventName
                : item.EventIsAuto ? "Автособытие не использовано: " + item.EventName : item.EventName;
        }
        return Path.Combine(captureDate.Year.ToString("0000"), GetMonthAbbreviation(captureDate.Month));
    }

    private static string BuildQuickMonthFolder(int month) =>
        month is >= 1 and <= 12 ? $"{month:00} — {RussianMonthNames[month - 1]}" : "00 — месяц";

    private static readonly string[] RussianMonthNames =
    [
        "январь", "февраль", "март", "апрель", "май", "июнь",
        "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь"
    ];

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

    private static async Task TransferVerifiedAsync(
        string source,
        string destination,
        string expectedSha256,
        DateTime destinationCreationUtc,
        DateTime destinationLastWriteUtc,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("Исходный файл не найден.", source);
        if (File.Exists(destination)) throw new IOException("Целевой файл уже существует. Перезапись запрещена: " + destination);

        var sourceInfo = new FileInfo(source);
        var sourceCreationUtc = sourceInfo.CreationTimeUtc;
        var sourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
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
                // Full SHA-256 on the FINAL path proves that all embedded bytes survived unchanged,
                // including EXIF/IPTC/XMP metadata and ICC profiles.
                await VerifyFinalTransferredFileAsync(
                    destination, expectedSha256, destinationCreationUtc, destinationLastWriteUtc, cancellationToken);
            }
            catch
            {
                try
                {
                    if (File.Exists(destination) && !File.Exists(source))
                    {
                        var rollbackHash = await ComputeSha256Async(destination, CancellationToken.None);
                        if (string.Equals(rollbackHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Move(destination, source);
                            ApplyAndVerifyFileDates(source, sourceCreationUtc, sourceLastWriteUtc);
                        }
                        else
                        {
                            LoggingService.Error("CRITICAL: same-volume rollback refused to move a destination whose SHA-256 no longer matches the transferred file.");
                        }
                    }
                }
                catch (Exception rollbackEx) { LoggingService.Error("CRITICAL: same-volume move rollback failed", rollbackEx); }
                throw;
            }
            return;
        }

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

            // Freeze the exact source object by an atomic same-volume rename before anything is
            // deleted. A different process can no longer replace source and make PAM delete a new
            // file that merely appeared under the old path. The FINAL destination is verified below.
            File.Move(source, sourceTombstone);
            sourceTombstoned = true;
            var tombstoneHash = await ComputeSha256Async(sourceTombstone, cancellationToken);
            if (!string.Equals(tombstoneHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Исходный файл изменился во время переноса. Удаление отменено.");

            if (File.Exists(destination))
                throw new IOException("Целевой файл появился во время копирования. Перезапись запрещена.");
            File.Move(temp, destination);
            destinationCreated = true;

            // Verify the actual final object, not just the temporary copy. The source tombstone is
            // deleted only after this final SHA-256 + filesystem-date verification succeeds.
            await VerifyFinalTransferredFileAsync(
                destination, expectedSha256, destinationCreationUtc, destinationLastWriteUtc, cancellationToken);

            var finalTombstoneHash = await ComputeSha256Async(sourceTombstone, CancellationToken.None);
            if (!string.Equals(finalTombstoneHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Исходный tombstone изменился непосредственно перед удалением. Автоматическое удаление отменено.");
            File.Delete(sourceTombstone);
            if (File.Exists(sourceTombstone))
                throw new IOException("После проверенного копирования Windows не удалила исходный tombstone.");
            sourceTombstoned = false;
        }
        catch
        {
            // Roll back only objects created/moved by this transfer. If another process has already
            // recreated the original path, never overwrite it; keep the verified tombstone for
            // manual recovery and surface the failure.
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
                LoggingService.Error("CRITICAL: cross-volume move rollback failed; source tombstone=" + sourceTombstone, rollbackEx);
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
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
            throw new IOException("Контрольный SHA-256 конечного файла после переноса не совпал. Исходник не будет удалён.");

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
        // FAT-family filesystems can quantize timestamps. Up to 2 seconds is the maximum historical
        // FAT write-time granularity; log the rounding but do not discard an otherwise verified photo.
        if (delta > TimeSpan.FromSeconds(2))
            throw new IOException($"Не удалось сохранить {label} файла после переноса: {path}");
        if (delta > TimeSpan.Zero)
            LoggingService.Warn($"Файловая система округлила {label} при переносе на {delta.TotalMilliseconds:0} мс: {path}");
    }

    private static DateTime ResolveDestinationCreationUtc(DateTime? embeddedCaptureDate, DateTime fallbackCreationUtc, string path)
    {
        if (!embeddedCaptureDate.HasValue) return fallbackCreationUtc;
        try
        {
            // EXIF DateTimeOriginal normally has no timezone. Treat its wall-clock value as local
            // Windows time so Explorer's 'Дата создания' displays the camera-recorded clock value.
            var cameraTime = embeddedCaptureDate.Value;
            var utc = cameraTime.Kind switch
            {
                DateTimeKind.Utc => cameraTime,
                DateTimeKind.Local => cameraTime.ToUniversalTime(),
                _ => DateTime.SpecifyKind(cameraTime, DateTimeKind.Local).ToUniversalTime()
            };
            _ = utc.ToFileTimeUtc(); // Validate Windows filesystem range before touching the source.
            return utc;
        }
        catch (ArgumentException)
        {
            LoggingService.Warn("EXIF-дата не подходит для CreationTime Windows; сохранена исходная дата файла: " + path);
            return fallbackCreationUtc;
        }
    }

    private static bool TryDateTimeFromTicks(long ticks, out DateTime value)
    {
        if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
        {
            value = default;
            return false;
        }
        value = new DateTime(ticks, DateTimeKind.Utc);
        return true;
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
