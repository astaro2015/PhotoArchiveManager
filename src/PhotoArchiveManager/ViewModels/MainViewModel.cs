using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using PhotoArchiveManager;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;
using PhotoArchiveManager.Services;

namespace PhotoArchiveManager.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int PageSize = 500;
    private const string ReviewAll = "Все";
    private const string ReviewNeeds = "Требуют разбора";
    private const string ReviewUntrustedDate = "Нет надёжной даты";
    private const string ReviewWithoutEvent = "Нет события";
    private const string ReviewIndexError = "Ошибка индексации";
    private const string SortDateDescending = "Дата ↓";
    private const string SortDateAscending = "Дата ↑";
    private const string SortFileName = "Имя";
    private const string SortFullPath = "Путь";
    private const string OrganizationLayoutYear = "Быстрая: только год";
    private const string OrganizationLayoutYearMonth = "Быстрая: год → месяц";
    private const string OrganizationLayoutYearMonthDay = "Быстрая: год → месяц → день";
    private const string OrganizationLayoutFull = "Полная: год → месяц/событие";
    private const string DuplicateViewGroups = "По группам файлов";
    private const string DuplicateViewFolders = "По папкам";
    private readonly DatabaseService _database;
    private readonly LibraryScanner _scanner;
    private readonly ExactDuplicateAnalyzer _duplicateAnalyzer;
    private readonly PerceptualHashAnalyzer _perceptualAnalyzer;
    private readonly QualityAnalyzer _qualityAnalyzer;
    private readonly BurstAnalyzer _burstAnalyzer;
    private readonly PeopleAnalyzer _peopleAnalyzer;
    private readonly EventAnalyzer _eventAnalyzer;
    private readonly OrganizationService _organizationService;
    private readonly QuarantineService _quarantineService;
    private CancellationTokenSource? _scanCts;


    public ObservableCollection<SourceFolderItem> Sources { get; } = new();
    public ObservableCollection<PhotoItem> Photos { get; } = new();
    public ObservableCollection<string> Years { get; } = new();
    public ObservableCollection<string> Cameras { get; } = new();
    public ObservableCollection<DuplicateGroupItem> DuplicateGroups { get; } = new();
    public ObservableCollection<DuplicateFolderItem> DuplicateFolderGroups { get; } = new();
    public ObservableCollection<DuplicateFolderIntersectionItem> DuplicateFolderIntersections { get; } = new();
    public ObservableCollection<DuplicateFolderPairItem> DuplicateFolderPairRows { get; } = new();
    public ObservableCollection<VisualDuplicateGroupItem> VisualDuplicateGroups { get; } = new();
    public ObservableCollection<BurstGroupItem> BurstGroups { get; } = new();
    public ObservableCollection<PersonGroupItem> PersonGroups { get; } = new();
    public ObservableCollection<FaceItem> PersonFaces { get; } = new();
    public ObservableCollection<EventGroupItem> EventGroups { get; } = new();
    public ObservableCollection<ArchiveYearItem> EventYears { get; } = new();
    public ObservableCollection<PhotoItem> EventPhotos { get; } = new();
    public ObservableCollection<TimelineYearItem> TimelineYears { get; } = new();
    public ObservableCollection<PhotoItem> TimelinePhotos { get; } = new();
    public ObservableCollection<OrganizationPlanItem> OrganizationPlan { get; } = new();
    public ObservableCollection<OrganizationActionItem> OrganizationMoves { get; } = new();
    public ObservableCollection<QuarantineActionItem> QuarantineActions { get; } = new();
    public IReadOnlyList<string> DuplicateViewModeOptions { get; } = new[] { DuplicateViewGroups, DuplicateViewFolders };
    public IReadOnlyList<int> VisualThresholdOptions { get; } = new[] { 2, 3, 4, 5, 6, 7 };
    public IReadOnlyList<int> BurstGapOptions { get; } = new[] { 5, 10, 15, 30, 60 };
    public IReadOnlyList<int> BurstMinSizeOptions { get; } = new[] { 3, 4, 5, 8 };
    public IReadOnlyList<int> BurstKeepCountOptions { get; } = new[] { 1, 2, 3 };
    public IReadOnlyList<double> PeopleThresholdOptions { get; } = new[] { 0.40, 0.45, 0.50, 0.55, 0.60, 0.65 };
    public IReadOnlyList<int> PeopleMinGroupSizeOptions { get; } = new[] { 2, 3, 4, 5, 8 };
    public IReadOnlyList<int> EventGapOptions { get; } = new[] { 30, 60, 90, 120, 180, 240 };
    public IReadOnlyList<int> EventMinSizeOptions { get; } = new[] { 2, 3, 4, 5, 8 };
    public IReadOnlyList<string> ReviewFilterOptions { get; } = new[]
    {
        ReviewAll, ReviewNeeds, ReviewUntrustedDate, ReviewWithoutEvent, ReviewIndexError
    };
    public IReadOnlyList<string> PhotoSortOptions { get; } = new[]
    {
        SortDateDescending, SortDateAscending, SortFileName, SortFullPath
    };
    public IReadOnlyList<string> OrganizationLayoutOptions { get; } = new[]
    {
        OrganizationLayoutYear, OrganizationLayoutYearMonth, OrganizationLayoutYearMonthDay, OrganizationLayoutFull
    };

    private SourceFolderItem? _selectedSource;
    public SourceFolderItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
                OnPropertyChanged(nameof(CanManageSelectedSource));
        }
    }

    public bool CanManageSelectedSource => SelectedSource is not null
        && !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning
        && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning
        && !IsEventAnalysisRunning && !IsFileOperationRunning;

    public bool CanMoveEventPhotos => CanPerformCatalogReview;

    public bool CanPerformCatalogReview => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning
        && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning
        && !IsEventAnalysisRunning && !IsFileOperationRunning;

    private PhotoItem? _selectedPhoto;
    public PhotoItem? SelectedPhoto
    {
        get => _selectedPhoto;
        set
        {
            if (SetProperty(ref _selectedPhoto, value))
                RaiseViewerCommands();
        }
    }

    private DuplicateGroupItem? _selectedDuplicateGroup;
    public DuplicateGroupItem? SelectedDuplicateGroup
    {
        get => _selectedDuplicateGroup;
        set
        {
            if (SetProperty(ref _selectedDuplicateGroup, value))
            {
                SelectedDuplicateFile = value?.Files.FirstOrDefault();
                QuarantineOtherCopiesCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private DuplicateFileItem? _selectedDuplicateFile;
    public DuplicateFileItem? SelectedDuplicateFile
    {
        get => _selectedDuplicateFile;
        set
        {
            if (SetProperty(ref _selectedDuplicateFile, value))
                RaiseDuplicateViewerCommands();
        }
    }

    private string _selectedDuplicateViewMode = DuplicateViewGroups;
    public string SelectedDuplicateViewMode
    {
        get => _selectedDuplicateViewMode;
        set
        {
            if (SetProperty(ref _selectedDuplicateViewMode, value))
            {
                OnPropertyChanged(nameof(IsDuplicateGroupView));
                OnPropertyChanged(nameof(IsDuplicateFolderView));
            }
        }
    }

    public bool IsDuplicateGroupView => !string.Equals(SelectedDuplicateViewMode, DuplicateViewFolders, StringComparison.Ordinal);
    public bool IsDuplicateFolderView => string.Equals(SelectedDuplicateViewMode, DuplicateViewFolders, StringComparison.Ordinal);

    private bool _deleteExactDuplicatesDirectly;
    public bool DeleteExactDuplicatesDirectly
    {
        get => _deleteExactDuplicatesDirectly;
        set
        {
            if (!SetProperty(ref _deleteExactDuplicatesDirectly, value)) return;
            OnPropertyChanged(nameof(DuplicateOtherCopiesActionText));
            OnPropertyChanged(nameof(DuplicatePairActionText));
            OnPropertyChanged(nameof(ExactDuplicateRemovalModeText));
            OnPropertyChanged(nameof(ExactDuplicateGroupSafetyText));
            StatusText = value
                ? "ВНИМАНИЕ: для точных дублей включено прямое безвозвратное удаление. Карантин и Undo использоваться не будут."
                : "Для точных дублей включён безопасный режим карантина с Undo.";
        }
    }

    public string DuplicateOtherCopiesActionText => DeleteExactDuplicatesDirectly
        ? "Оставить этот · остальные УДАЛИТЬ"
        : "Оставить этот · остальные в карантин";
    public string DuplicatePairActionText
    {
        get
        {
            var side = RemoveDuplicatePairFromLeft ? "A" : "B";
            return DeleteExactDuplicatesDirectly
                ? $"Удалить дубли из выбранной стороны {side}"
                : $"В карантин дубли из выбранной стороны {side}";
        }
    }
    public string ExactDuplicateRemovalModeText => DeleteExactDuplicatesDirectly
        ? "ОПАСНЫЙ РЕЖИМ: файлы удаляются напрямую и без Undo."
        : "Безопасный режим: файлы перемещаются в карантин и могут быть возвращены через Undo.";
    public string ExactDuplicateGroupSafetyText => DeleteExactDuplicatesDirectly
        ? "Перед удалением PAM повторно проверяет SHA-256 сохраняемой и удаляемых копий. Удаление безвозвратное: карантин и Undo отключены."
        : "SHA-256 повторно проверяется перед карантином. Перенос обратим через Undo.";
    private DuplicateFolderIntersectionItem? _selectedDuplicateFolderIntersection;
    public DuplicateFolderIntersectionItem? SelectedDuplicateFolderIntersection
    {
        get => _selectedDuplicateFolderIntersection;
        set
        {
            if (!SetProperty(ref _selectedDuplicateFolderIntersection, value)) return;

            var left = value is null
                ? null
                : DuplicateFolderGroups.FirstOrDefault(x =>
                    string.Equals(x.FolderPath, value.FolderAPath, StringComparison.OrdinalIgnoreCase));
            SelectedDuplicateFolder = left;

            var match = left?.Matches.FirstOrDefault(x =>
                string.Equals(x.OtherFolderPath, value?.FolderBPath, StringComparison.OrdinalIgnoreCase));
            SelectedDuplicateFolderMatch = match;

            RemoveDuplicatePairFromLeft = true;
            OnPropertyChanged(nameof(SelectedDuplicateFolderRight));
            OnPropertyChanged(nameof(DuplicatePairSummaryText));
            OnPropertyChanged(nameof(DuplicatePairSelectionText));
            ShowDuplicateFolderInExplorerCommand?.RaiseCanExecuteChanged();
            ShowDuplicateFolderMatchInExplorerCommand?.RaiseCanExecuteChanged();
            QuarantineDuplicateFolderPairSideCommand?.RaiseCanExecuteChanged();
        }
    }

    private DuplicateFolderItem? _selectedDuplicateFolder;
    public DuplicateFolderItem? SelectedDuplicateFolder
    {
        get => _selectedDuplicateFolder;
        set
        {
            if (SetProperty(ref _selectedDuplicateFolder, value))
            {
                OnPropertyChanged(nameof(SelectedDuplicateFolderRight));
                RebuildDuplicateFolderPairRows();
                ShowDuplicateFolderInExplorerCommand?.RaiseCanExecuteChanged();
                QuarantineDuplicateFolderPairSideCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private DuplicateFolderMatchItem? _selectedDuplicateFolderMatch;
    public DuplicateFolderMatchItem? SelectedDuplicateFolderMatch
    {
        get => _selectedDuplicateFolderMatch;
        set
        {
            if (SetProperty(ref _selectedDuplicateFolderMatch, value))
            {
                RebuildDuplicateFolderPairRows();
                ShowDuplicateFolderMatchInExplorerCommand?.RaiseCanExecuteChanged();
                QuarantineDuplicateFolderPairSideCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public DuplicateFolderItem? SelectedDuplicateFolderRight
    {
        get
        {
            var path = SelectedDuplicateFolderIntersection?.FolderBPath;
            return string.IsNullOrWhiteSpace(path)
                ? null
                : DuplicateFolderGroups.FirstOrDefault(x =>
                    string.Equals(x.FolderPath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    private bool _showOnlyCompleteDuplicateFolderPairs;
    public bool ShowOnlyCompleteDuplicateFolderPairs
    {
        get => _showOnlyCompleteDuplicateFolderPairs;
        set
        {
            if (SetProperty(ref _showOnlyCompleteDuplicateFolderPairs, value))
                RebuildDuplicateFolderIntersections();
        }
    }

    private bool _removeDuplicatePairFromLeft = true;
    public bool RemoveDuplicatePairFromLeft
    {
        get => _removeDuplicatePairFromLeft;
        set
        {
            if (SetProperty(ref _removeDuplicatePairFromLeft, value))
            {
                OnPropertyChanged(nameof(RemoveDuplicatePairFromRight));
                OnPropertyChanged(nameof(DuplicatePairSelectionText));
                OnPropertyChanged(nameof(DuplicatePairActionText));
            }
        }
    }

    public bool RemoveDuplicatePairFromRight
    {
        get => !RemoveDuplicatePairFromLeft;
        set
        {
            if (value) RemoveDuplicatePairFromLeft = false;
        }
    }

    public string DuplicatePairSummaryText
    {
        get
        {
            var pair = SelectedDuplicateFolderIntersection;
            if (pair is null)
                return "Выберите слева пересечение — справа появятся папки A и B, а ниже их точные совпадения.";

            return $"Пара №{pair.DisplayNumber:N0}: {pair.SharedSetCount:N0} одинаковых SHA-256 · " +
                   $"A: {pair.ACopyCount:N0} ({ByteFormatter.Format(pair.ABytes)}) · " +
                   $"B: {pair.BCopyCount:N0} ({ByteFormatter.Format(pair.BBytes)})";
        }
    }

    public string DuplicatePairSelectionText
    {
        get
        {
            if (DuplicateFolderPairRows.Count == 0) return "Нет выбранной пары папок.";
            var count = RemoveDuplicatePairFromLeft
                ? DuplicateFolderPairRows.Sum(x => x.LeftCopyCount)
                : DuplicateFolderPairRows.Sum(x => x.RightCopyCount);
            var bytes = RemoveDuplicatePairFromLeft
                ? DuplicateFolderPairRows.Sum(x => x.LeftBytes)
                : DuplicateFolderPairRows.Sum(x => x.RightBytes);
            var side = RemoveDuplicatePairFromLeft ? "A" : "B";
            return $"Выбрана сторона {side}: будет убрано {count:N0} файлов · {ByteFormatter.Format(bytes)} только из этой пары.";
        }
    }

    private VisualDuplicateGroupItem? _selectedVisualDuplicateGroup;
    public VisualDuplicateGroupItem? SelectedVisualDuplicateGroup
    {
        get => _selectedVisualDuplicateGroup;
        set
        {
            if (SetProperty(ref _selectedVisualDuplicateGroup, value))
            {
                SelectedVisualDuplicateFile = value?.Files.FirstOrDefault();
                RaiseVisualCommands();
            }
        }
    }

    private VisualDuplicateFileItem? _selectedVisualDuplicateFile;
    public VisualDuplicateFileItem? SelectedVisualDuplicateFile
    {
        get => _selectedVisualDuplicateFile;
        set
        {
            if (SetProperty(ref _selectedVisualDuplicateFile, value))
                RaiseVisualViewerCommands();
        }
    }

    private BurstGroupItem? _selectedBurstGroup;
    public BurstGroupItem? SelectedBurstGroup
    {
        get => _selectedBurstGroup;
        set
        {
            if (SetProperty(ref _selectedBurstGroup, value))
            {
                if (value is not null) value.ApplyRecommendations(SelectedBurstKeepCount);
                SelectedBurstFile = value?.Files.FirstOrDefault();
                RaiseBurstCommands();
            }
        }
    }

    private VisualDuplicateFileItem? _selectedBurstFile;
    public VisualDuplicateFileItem? SelectedBurstFile
    {
        get => _selectedBurstFile;
        set
        {
            if (SetProperty(ref _selectedBurstFile, value))
                RaiseBurstViewerCommands();
        }
    }

    private readonly HashSet<long> _selectedPersonGroupIds = new();
    private long? _preferredPersonMergeTargetId;
    private PersonGroupItem? _selectedPersonGroup;
    public PersonGroupItem? SelectedPersonGroup
    {
        get => _selectedPersonGroup;
        set
        {
            if (SetProperty(ref _selectedPersonGroup, value))
            {
                _ = LoadSelectedPersonFacesAsync();
                RaisePeopleCommands();
                NotifyPersonSelectionChanged();
            }
        }
    }

    public void UpdateSelectedPersonGroups(IEnumerable<PersonGroupItem> groups)
    {
        _selectedPersonGroupIds.Clear();
        foreach (var group in groups)
            _selectedPersonGroupIds.Add(group.Id);

        if (_preferredPersonMergeTargetId.HasValue && !_selectedPersonGroupIds.Contains(_preferredPersonMergeTargetId.Value))
            _preferredPersonMergeTargetId = null;

        NotifyPersonSelectionChanged();
        RaisePeopleCommands();
    }

    public void SetPreferredPersonMergeTarget(PersonGroupItem? group)
    {
        _preferredPersonMergeTargetId = group is { Id: > 0 } ? group.Id : null;
    }

    private PersonGroupItem? GetPreferredPersonMergeTarget(IReadOnlyList<PersonGroupItem> candidates)
    {
        if (_preferredPersonMergeTargetId.HasValue)
        {
            var preferred = candidates.FirstOrDefault(x => x.Id == _preferredPersonMergeTargetId.Value);
            if (preferred is not null) return preferred;
        }

        if (SelectedPersonGroup is { Id: > 0 } current)
        {
            var selected = candidates.FirstOrDefault(x => x.Id == current.Id);
            if (selected is not null) return selected;
        }

        // If there is no explicit context target, a manually named group is a safer default
        // keeper than an anonymous auto-group because its meaningful name should normally survive.
        return candidates.FirstOrDefault(x => x.IsNamed) ?? candidates.FirstOrDefault();
    }

    private List<PersonGroupItem> GetSelectedPersonGroups()
    {
        var selected = PersonGroups.Where(x => _selectedPersonGroupIds.Contains(x.Id)).ToList();
        if (selected.Count == 0 && SelectedPersonGroup is not null)
            selected.Add(SelectedPersonGroup);
        return selected;
    }

    private int SelectedRealPersonGroupCount => GetSelectedPersonGroups().Count(x => x.Id > 0);
    private bool HasMultipleSelectedPersonGroups => GetSelectedPersonGroups().Count > 1;

    public string PersonSelectionSummaryText
    {
        get
        {
            var selected = GetSelectedPersonGroups();
            if (selected.Count == 0) return "Ничего не выбрано.";
            if (selected.Count == 1)
                return $"Выбрано: {selected[0].DisplayName} · лиц: {selected[0].FaceCount:N0}.";
            var real = selected.Count(x => x.Id > 0);
            var ungrouped = selected.Any(x => x.Id == 0);
            var kind = ungrouped ? $"{real:N0} групп + «Без группы»" : $"{real:N0} групп";
            var note = ungrouped ? " «Без группы» можно исключить, но её нельзя объединить или расформировать." : "";
            return $"Выбрано: {selected.Count:N0} элементов ({kind}) · лиц: {selected.Sum(x => x.FaceCount):N0}. Групповые действия применяются ко всему выделению.{note}";
        }
    }

    public string MergePersonActionText => SelectedRealPersonGroupCount >= 2
        ? $"Объединить выбранные группы ({SelectedRealPersonGroupCount:N0})…"
        : "Объединить эту группу с другой…";
    public string DeletePersonActionText => GetSelectedPersonGroups().Count >= 2 && SelectedRealPersonGroupCount > 0
        ? $"Расформировать выбранные группы ({SelectedRealPersonGroupCount:N0})…"
        : "Расформировать группу (лица → «Без группы»)…";
    public string IgnorePersonActionText => GetSelectedPersonGroups().Count >= 2
        ? $"Исключить выбранные элементы ({GetSelectedPersonGroups().Count:N0}) из раздела «Люди»…"
        : "Исключить лица группы из раздела «Люди»…";

    private void NotifyPersonSelectionChanged()
    {
        OnPropertyChanged(nameof(PersonSelectionSummaryText));
        OnPropertyChanged(nameof(MergePersonActionText));
        OnPropertyChanged(nameof(DeletePersonActionText));
        OnPropertyChanged(nameof(IgnorePersonActionText));
    }

    private bool HasSingleSelectedRealPersonGroup()
    {
        var selected = GetSelectedPersonGroups();
        return selected.Count == 1 && selected[0].Id > 0;
    }

    private bool CanMergeCurrentPersonSelection()
    {
        var selected = GetSelectedPersonGroups();
        if (selected.Count >= 2) return selected.Count(x => x.Id > 0) >= 2;
        return selected.Count == 1 && selected[0].Id > 0 && PersonGroups.Any(x => x.Id > 0 && x.Id != selected[0].Id);
    }

    private bool CanDeleteCurrentPersonSelection() => GetSelectedPersonGroups().Any(x => x.Id > 0);
    private bool CanIgnoreCurrentPersonSelection() => GetSelectedPersonGroups().Any(x => x.FaceCount > 0);

    private FaceItem? _selectedPersonFace;
    public FaceItem? SelectedPersonFace
    {
        get => _selectedPersonFace;
        set
        {
            if (SetProperty(ref _selectedPersonFace, value))
                RaisePeopleCommands();
        }
    }

    private EventGroupItem? _selectedEventGroup;
    public EventGroupItem? SelectedEventGroup
    {
        get => _selectedEventGroup;
        set
        {
            if (SetProperty(ref _selectedEventGroup, value))
            {
                _ = LoadSelectedEventPhotosAsync();
                RaiseEventCommands();
            }
        }
    }

    private PhotoItem? _selectedEventPhoto;
    public PhotoItem? SelectedEventPhoto
    {
        get => _selectedEventPhoto;
        set
        {
            if (SetProperty(ref _selectedEventPhoto, value))
                RaiseEventCommands();
        }
    }

    private TimelineMonthItem? _selectedTimelineMonth;
    private bool _suppressTimelinePhotoLoad;
    public TimelineMonthItem? SelectedTimelineMonth
    {
        get => _selectedTimelineMonth;
        set
        {
            if (SetProperty(ref _selectedTimelineMonth, value))
            {
                OnPropertyChanged(nameof(TimelineSummaryText));
                if (!_suppressTimelinePhotoLoad && SelectedMainTabIndex == 6)
                    _ = LoadSelectedTimelineMonthAsync();
            }
        }
    }

    private PhotoItem? _selectedTimelinePhoto;
    public PhotoItem? SelectedTimelinePhoto
    {
        get => _selectedTimelinePhoto;
        set => SetProperty(ref _selectedTimelinePhoto, value);
    }

    public string TimelineSummaryText => SelectedTimelineMonth is null
        ? "Выберите месяц слева."
        : $"{SelectedTimelineMonth.DisplayName} · хронология по дате каталога";

    private FaceIgnoreActionItem? _latestFaceIgnoreAction;
    public FaceIgnoreActionItem? LatestFaceIgnoreAction
    {
        get => _latestFaceIgnoreAction;
        private set
        {
            if (SetProperty(ref _latestFaceIgnoreAction, value))
            {
                OnPropertyChanged(nameof(FaceIgnoreUndoText));
                UndoLastFaceIgnoreCommand?.RaiseCanExecuteChanged();
            }
        }
    }
    public string FaceIgnoreUndoText => LatestFaceIgnoreAction?.DisplayText ?? "Нет исключений для восстановления";

    private QuarantineActionItem? _selectedQuarantineAction;
    public QuarantineActionItem? SelectedQuarantineAction
    {
        get => _selectedQuarantineAction;
        set
        {
            if (SetProperty(ref _selectedQuarantineAction, value))
                RaiseQuarantineCommands();
        }
    }

    private string _searchText = "";
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }

    private string _selectedYear = "Все";
    public string SelectedYear { get => _selectedYear; set => SetProperty(ref _selectedYear, value); }

    private string _selectedCamera = "Все";
    public string SelectedCamera { get => _selectedCamera; set => SetProperty(ref _selectedCamera, value); }

    private string _selectedReviewFilter = ReviewAll;
    public string SelectedReviewFilter { get => _selectedReviewFilter; set => SetProperty(ref _selectedReviewFilter, value); }

    private string _selectedPhotoSort = SortDateDescending;
    public string SelectedPhotoSort { get => _selectedPhotoSort; set => SetProperty(ref _selectedPhotoSort, value); }

    private string _statusText = "Готово.";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string _scanCountersText = "";
    public string ScanCountersText { get => _scanCountersText; set => SetProperty(ref _scanCountersText, value); }

    private string _statisticsText = "Каталог пуст.";
    public string StatisticsText { get => _statisticsText; set => SetProperty(ref _statisticsText, value); }

    private string _loadedText = "";
    public string LoadedText { get => _loadedText; set => SetProperty(ref _loadedText, value); }

    private string _listHeaderText = "Фотографии";
    public string ListHeaderText { get => _listHeaderText; set => SetProperty(ref _listHeaderText, value); }

    private string _duplicateSummaryText = "Точные дубли ещё не анализировались.";
    public string DuplicateSummaryText { get => _duplicateSummaryText; set => SetProperty(ref _duplicateSummaryText, value); }

    private string _duplicateProgressText = "";
    public string DuplicateProgressText { get => _duplicateProgressText; set => SetProperty(ref _duplicateProgressText, value); }

    private string _visualDuplicateSummaryText = "Визуальные дубли ещё не анализировались.";
    public string VisualDuplicateSummaryText { get => _visualDuplicateSummaryText; set => SetProperty(ref _visualDuplicateSummaryText, value); }

    private string _visualDuplicateProgressText = "";
    public string VisualDuplicateProgressText { get => _visualDuplicateProgressText; set => SetProperty(ref _visualDuplicateProgressText, value); }

    private string _qualityProgressText = "";
    public string QualityProgressText { get => _qualityProgressText; set => SetProperty(ref _qualityProgressText, value); }

    private string _burstSummaryText = "Серии ещё не анализировались.";
    public string BurstSummaryText { get => _burstSummaryText; set => SetProperty(ref _burstSummaryText, value); }

    private string _burstProgressText = "";
    public string BurstProgressText { get => _burstProgressText; set => SetProperty(ref _burstProgressText, value); }

    private string _burstQualityProgressText = "";
    public string BurstQualityProgressText { get => _burstQualityProgressText; set => SetProperty(ref _burstQualityProgressText, value); }

    private int _selectedBurstMaxGap = 15;
    public int SelectedBurstMaxGap { get => _selectedBurstMaxGap; set => SetProperty(ref _selectedBurstMaxGap, Math.Clamp(value, 2, 120)); }

    private int _selectedBurstMinSize = 3;
    public int SelectedBurstMinSize { get => _selectedBurstMinSize; set => SetProperty(ref _selectedBurstMinSize, Math.Clamp(value, 3, 20)); }

    private int _selectedBurstKeepCount = 1;
    public int SelectedBurstKeepCount
    {
        get => _selectedBurstKeepCount;
        set
        {
            if (SetProperty(ref _selectedBurstKeepCount, Math.Clamp(value, 1, 3)))
            {
                SelectedBurstGroup?.ApplyRecommendations(_selectedBurstKeepCount);
                RaiseBurstCommands();
            }
        }
    }

    private int _selectedVisualThreshold = 6;
    public int SelectedVisualThreshold { get => _selectedVisualThreshold; set => SetProperty(ref _selectedVisualThreshold, Math.Clamp(value, 1, 7)); }

    private bool _allowPermanentDelete;
    public bool AllowPermanentDelete
    {
        get => _allowPermanentDelete;
        set
        {
            if (SetProperty(ref _allowPermanentDelete, value))
            {
                RaiseQuarantineCommands();
                StatusText = value
                    ? "Опасный режим включён на эту сессию. Окончательное удаление всё равно потребует двух подтверждений."
                    : "Окончательное удаление отключено.";
            }
        }
    }

    private string _peopleSummaryText = "Лица ещё не индексировались.";
    public string PeopleSummaryText { get => _peopleSummaryText; set => SetProperty(ref _peopleSummaryText, value); }

    private string _peopleProgressText = "";
    public string PeopleProgressText { get => _peopleProgressText; set => SetProperty(ref _peopleProgressText, value); }

    private double _selectedPeopleThreshold = 0.50;
    public double SelectedPeopleThreshold { get => _selectedPeopleThreshold; set => SetProperty(ref _selectedPeopleThreshold, Math.Clamp(value, 0.35, 0.80)); }

    private int _selectedPeopleMinGroupSize = 3;
    public int SelectedPeopleMinGroupSize { get => _selectedPeopleMinGroupSize; set => SetProperty(ref _selectedPeopleMinGroupSize, Math.Clamp(value, 2, 20)); }

    private string _eventSummaryText = "События ещё не анализировались.";
    public string EventSummaryText { get => _eventSummaryText; set => SetProperty(ref _eventSummaryText, value); }

    private string _eventProgressText = "";
    public string EventProgressText { get => _eventProgressText; set => SetProperty(ref _eventProgressText, value); }

    private int _selectedEventMaxGap = 120;
    public int SelectedEventMaxGap { get => _selectedEventMaxGap; set => SetProperty(ref _selectedEventMaxGap, Math.Clamp(value, 15, 720)); }

    private int _selectedEventMinSize = 2;
    public int SelectedEventMinSize { get => _selectedEventMinSize; set => SetProperty(ref _selectedEventMinSize, Math.Clamp(value, 2, 50)); }

    private string _selectedOrganizationLayout = OrganizationLayoutYear;
    public string SelectedOrganizationLayout
    {
        get => _selectedOrganizationLayout;
        set
        {
            var normalized = OrganizationLayoutOptions.Contains(value) ? value : OrganizationLayoutYear;
            if (SetProperty(ref _selectedOrganizationLayout, normalized))
            {
                OnPropertyChanged(nameof(OrganizationIsFullLayout));
                OnPropertyChanged(nameof(OrganizationLayoutDescription));
                InvalidateOrganizationPlan("Режим раскладки изменён — постройте preview заново.");
            }
        }
    }

    public bool OrganizationIsFullLayout => SelectedOrganizationLayout == OrganizationLayoutFull;

    public string OrganizationLayoutDescription => SelectedOrganizationLayout switch
    {
        OrganizationLayoutYear => @"Быстрая раскладка: 2024\фото.jpg. Только папки по годам; исходные имена файлов сохраняются.",
        OrganizationLayoutYearMonth => @"Быстрая раскладка: 2024\08 — август\фото.jpg. Месяцы имеют числовой префикс и сортируются по календарю.",
        OrganizationLayoutYearMonthDay => @"Быстрая раскладка: 2024\08 — август\18\фото.jpg. Подходит для очень больших ежедневных архивов.",
        _ => "Полная организация: год → месяц/событие. Здесь можно добавлять дату и имена людей в имя файла."
    };

    private OrganizationLayoutMode SelectedOrganizationLayoutMode => SelectedOrganizationLayout switch
    {
        OrganizationLayoutYearMonth => OrganizationLayoutMode.YearMonth,
        OrganizationLayoutYearMonthDay => OrganizationLayoutMode.YearMonthDay,
        OrganizationLayoutFull => OrganizationLayoutMode.Full,
        _ => OrganizationLayoutMode.Year
    };

    private string _organizationDestinationRoot = "";
    public string OrganizationDestinationRoot
    {
        get => _organizationDestinationRoot;
        set
        {
            if (SetProperty(ref _organizationDestinationRoot, value ?? ""))
            {
                InvalidateOrganizationPlan("Путь назначения изменён — постройте preview заново.");
                BuildOrganizationPlanCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _organizationUseFallbackDates;
    public bool OrganizationUseFallbackDates
    {
        get => _organizationUseFallbackDates;
        set
        {
            if (SetProperty(ref _organizationUseFallbackDates, value))
                InvalidateOrganizationPlan("Правила дат изменены — постройте preview заново.");
        }
    }

    private bool _organizationIncludeDateInFileName = true;
    public bool OrganizationIncludeDateInFileName
    {
        get => _organizationIncludeDateInFileName;
        set
        {
            if (SetProperty(ref _organizationIncludeDateInFileName, value))
                InvalidateOrganizationPlan("Шаблон имени файла изменён — постройте preview заново.");
        }
    }

    private bool _organizationIncludeNamedPeopleInFileName = true;
    public bool OrganizationIncludeNamedPeopleInFileName
    {
        get => _organizationIncludeNamedPeopleInFileName;
        set
        {
            if (SetProperty(ref _organizationIncludeNamedPeopleInFileName, value))
                InvalidateOrganizationPlan("Шаблон имени файла изменён — постройте preview заново.");
        }
    }

    private string _organizationSummaryText = "Выберите папку назначения и режим раскладки, затем постройте Preview. Быстрые режимы сохраняют исходные имена; полная организация умеет учитывать события и именованных людей.";
    public string OrganizationSummaryText { get => _organizationSummaryText; set => SetProperty(ref _organizationSummaryText, value); }

    private string _organizationProgressText = "";
    public string OrganizationProgressText { get => _organizationProgressText; set => SetProperty(ref _organizationProgressText, value); }

    private OrganizationPlanItem? _selectedOrganizationPlanItem;
    public OrganizationPlanItem? SelectedOrganizationPlanItem
    {
        get => _selectedOrganizationPlanItem;
        set { if (SetProperty(ref _selectedOrganizationPlanItem, value)) RaiseOrganizationCommands(); }
    }

    private OrganizationActionItem? _selectedOrganizationMove;
    public OrganizationActionItem? SelectedOrganizationMove
    {
        get => _selectedOrganizationMove;
        set { if (SetProperty(ref _selectedOrganizationMove, value)) RaiseOrganizationCommands(); }
    }

    private bool _isOrganizationMoveRunning;
    public bool IsOrganizationMoveRunning
    {
        get => _isOrganizationMoveRunning;
        private set
        {
            if (SetProperty(ref _isOrganizationMoveRunning, value)) RaiseOrganizationCommands();
        }
    }

    private int _selectedMainTabIndex;
    public int SelectedMainTabIndex
    {
        get => _selectedMainTabIndex;
        set
        {
            if (SetProperty(ref _selectedMainTabIndex, value) && value == 6 && SelectedTimelineMonth is not null)
                _ = LoadSelectedTimelineMonthAsync();
        }
    }

    private long? _personFilterId;
    private string _personFilterName = "";

    private long? _eventFilterId;
    private string _eventFilterName = "";

    private string _quarantineSummaryText = "Карантин пуст.";
    public string QuarantineSummaryText { get => _quarantineSummaryText; set => SetProperty(ref _quarantineSummaryText, value); }

    private long _filteredTotal;

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }

    private bool _isDuplicateAnalysisRunning;
    public bool IsDuplicateAnalysisRunning
    {
        get => _isDuplicateAnalysisRunning;
        private set
        {
            if (SetProperty(ref _isDuplicateAnalysisRunning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }

    private bool _isVisualAnalysisRunning;
    public bool IsVisualAnalysisRunning
    {
        get => _isVisualAnalysisRunning;
        private set
        {
            if (SetProperty(ref _isVisualAnalysisRunning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }

    private bool _isQualityAnalysisRunning;
    public bool IsQualityAnalysisRunning
    {
        get => _isQualityAnalysisRunning;
        private set
        {
            if (SetProperty(ref _isQualityAnalysisRunning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }

    private bool _isBurstAnalysisRunning;
    public bool IsBurstAnalysisRunning
    {
        get => _isBurstAnalysisRunning;
        private set
        {
            if (SetProperty(ref _isBurstAnalysisRunning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }

    private bool _isPeopleAnalysisRunning;
    public bool IsPeopleAnalysisRunning
    {
        get => _isPeopleAnalysisRunning;
        private set
        {
            if (SetProperty(ref _isPeopleAnalysisRunning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }

    private bool _isEventAnalysisRunning;
    public bool IsEventAnalysisRunning
    {
        get => _isEventAnalysisRunning;
        private set
        {
            if (SetProperty(ref _isEventAnalysisRunning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }


    private bool _isFileOperationRunning;
    public bool IsFileOperationRunning
    {
        get => _isFileOperationRunning;
        private set
        {
            if (SetProperty(ref _isFileOperationRunning, value))
            {
                RaiseScanCommands();
                RaiseDuplicateCommands();
                RaiseVisualCommands();
                RaiseBurstCommands();
                RaisePeopleCommands();
                RaiseEventCommands();
                RaiseOrganizationCommands();
                RaiseQuarantineCommands();
                OnPropertyChanged(nameof(CanManageSelectedSource));
                OnPropertyChanged(nameof(CanPerformCatalogReview));
            }
        }
    }

    public string DataPathText => "Данные программы: " + AppPaths.DataDirectory;

    public AsyncRelayCommand StartScanCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand StopCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyFiltersCommand { get; }
    public AsyncRelayCommand ResetFiltersCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public RelayCommand ShowInExplorerCommand { get; }
    public RelayCommand OpenPhotoCommand { get; }
    public AsyncRelayCommand EditPhotoCaptureDateCommand { get; }
    public AsyncRelayCommand ResetPhotoCaptureDateCommand { get; }

    public AsyncRelayCommand FindExactDuplicatesCommand { get; }
    public RelayCommand PauseDuplicateAnalysisCommand { get; }
    public RelayCommand ResumeDuplicateAnalysisCommand { get; }
    public RelayCommand StopDuplicateAnalysisCommand { get; }
    public AsyncRelayCommand RefreshDuplicatesCommand { get; }
    public RelayCommand ShowDuplicateInExplorerCommand { get; }
    public RelayCommand OpenDuplicateFileCommand { get; }
    public AsyncRelayCommand QuarantineOtherCopiesCommand { get; }
    public RelayCommand ShowDuplicateFolderInExplorerCommand { get; }
    public RelayCommand ShowDuplicateFolderMatchInExplorerCommand { get; }
    public AsyncRelayCommand QuarantineDuplicateFolderPairSideCommand { get; }

    public AsyncRelayCommand FindVisualDuplicatesCommand { get; }
    public AsyncRelayCommand RefreshVisualDuplicatesCommand { get; }
    public RelayCommand PauseVisualAnalysisCommand { get; }
    public RelayCommand ResumeVisualAnalysisCommand { get; }
    public RelayCommand StopVisualAnalysisCommand { get; }
    public RelayCommand ShowVisualInExplorerCommand { get; }
    public RelayCommand OpenVisualFileCommand { get; }
    public AsyncRelayCommand AnalyzeSelectedQualityCommand { get; }
    public RelayCommand PauseQualityAnalysisCommand { get; }
    public RelayCommand ResumeQualityAnalysisCommand { get; }
    public RelayCommand StopQualityAnalysisCommand { get; }
    public RelayCommand ClearVisualReviewCommand { get; }
    public AsyncRelayCommand QuarantineMarkedVisualCommand { get; }

    public AsyncRelayCommand FindBurstsCommand { get; }
    public AsyncRelayCommand RefreshBurstsCommand { get; }
    public RelayCommand PauseBurstAnalysisCommand { get; }
    public RelayCommand ResumeBurstAnalysisCommand { get; }
    public RelayCommand StopBurstAnalysisCommand { get; }
    public RelayCommand ShowBurstInExplorerCommand { get; }
    public RelayCommand OpenBurstFileCommand { get; }
    public AsyncRelayCommand AnalyzeSelectedBurstQualityCommand { get; }
    public RelayCommand ApplyBurstRecommendationCommand { get; }
    public RelayCommand MarkBurstOthersCommand { get; }
    public RelayCommand ClearBurstReviewCommand { get; }
    public AsyncRelayCommand QuarantineMarkedBurstCommand { get; }

    public AsyncRelayCommand AnalyzePeopleCommand { get; }
    public RelayCommand PausePeopleAnalysisCommand { get; }
    public RelayCommand ResumePeopleAnalysisCommand { get; }
    public RelayCommand StopPeopleAnalysisCommand { get; }
    public AsyncRelayCommand GroupPeopleCommand { get; }
    public AsyncRelayCommand RefreshPeopleCommand { get; }
    public AsyncRelayCommand RenamePersonCommand { get; }
    public AsyncRelayCommand AssignFaceToPersonCommand { get; }
    public AsyncRelayCommand MergePersonGroupCommand { get; }
    public AsyncRelayCommand DeletePersonGroupCommand { get; }
    public AsyncRelayCommand RemoveFaceFromPersonCommand { get; }
    public AsyncRelayCommand IgnoreFaceCommand { get; }
    public AsyncRelayCommand IgnorePersonGroupCommand { get; }
    public AsyncRelayCommand UndoLastFaceIgnoreCommand { get; }
    public AsyncRelayCommand ShowPersonPhotosCommand { get; }
    public RelayCommand ShowPersonFaceInExplorerCommand { get; }
    public RelayCommand OpenPersonPhotoCommand { get; }
    public AsyncRelayCommand SetPersonCoverCommand { get; }

    public AsyncRelayCommand AnalyzeEventsCommand { get; }
    public RelayCommand PauseEventAnalysisCommand { get; }
    public RelayCommand ResumeEventAnalysisCommand { get; }
    public RelayCommand StopEventAnalysisCommand { get; }
    public AsyncRelayCommand RefreshEventsCommand { get; }
    public AsyncRelayCommand RenameEventCommand { get; }
    public AsyncRelayCommand MergeEventCommand { get; }
    public AsyncRelayCommand ShowEventPhotosCommand { get; }
    public RelayCommand ShowEventPhotoInExplorerCommand { get; }
    public RelayCommand OpenEventPhotoCommand { get; }
    public AsyncRelayCommand SetEventCoverCommand { get; }
    public AsyncRelayCommand EditEventNotesCommand { get; }
    public AsyncRelayCommand SplitEventCommand { get; }


    public AsyncRelayCommand BuildOrganizationPlanCommand { get; }
    public RelayCommand SelectTrustedOrganizationCommand { get; }
    public RelayCommand ClearOrganizationSelectionCommand { get; }
    public AsyncRelayCommand ExecuteOrganizationPlanCommand { get; }
    public RelayCommand PauseOrganizationCommand { get; }
    public RelayCommand ResumeOrganizationCommand { get; }
    public RelayCommand StopOrganizationCommand { get; }
    public AsyncRelayCommand RefreshOrganizationHistoryCommand { get; }
    public AsyncRelayCommand UndoOrganizationMoveCommand { get; }
    public RelayCommand ShowOrganizationSourceCommand { get; }
    public RelayCommand ShowOrganizationTargetCommand { get; }
    public RelayCommand ShowOrganizationMoveCommand { get; }

    public AsyncRelayCommand RefreshQuarantineCommand { get; }
    public AsyncRelayCommand UndoQuarantineCommand { get; }
    public AsyncRelayCommand PermanentDeleteQuarantineCommand { get; }
    public RelayCommand ShowQuarantineInExplorerCommand { get; }
    public RelayCommand OpenQuarantineFileCommand { get; }

    public MainViewModel(DatabaseService database, LibraryScanner scanner, ExactDuplicateAnalyzer duplicateAnalyzer, PerceptualHashAnalyzer perceptualAnalyzer, QualityAnalyzer qualityAnalyzer, BurstAnalyzer burstAnalyzer, PeopleAnalyzer peopleAnalyzer, EventAnalyzer eventAnalyzer, OrganizationService organizationService, QuarantineService quarantineService)
    {
        _database = database;
        _scanner = scanner;
        _duplicateAnalyzer = duplicateAnalyzer;
        _perceptualAnalyzer = perceptualAnalyzer;
        _qualityAnalyzer = qualityAnalyzer;
        _burstAnalyzer = burstAnalyzer;
        _peopleAnalyzer = peopleAnalyzer;
        _eventAnalyzer = eventAnalyzer;
        _organizationService = organizationService;
        _quarantineService = quarantineService;

        StartScanCommand = new AsyncRelayCommand(StartScanAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && Sources.Count > 0);
        PauseCommand = new RelayCommand(() =>
        {
            _scanner.Pause();
            StatusText = "Пауза. Текущий файл будет завершён, затем обработка остановится.";
            RaiseScanCommands();
        }, () => IsScanning && !_scanner.IsPaused);
        ResumeCommand = new RelayCommand(() =>
        {
            _scanner.Resume();
            StatusText = "Сканирование продолжено.";
            RaiseScanCommands();
        }, () => IsScanning && _scanner.IsPaused);
        StopCommand = new RelayCommand(() => _scanner.Stop(), () => IsScanning);
        RefreshCommand = new AsyncRelayCommand(RefreshAllAsync, () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        ApplyFiltersCommand = new AsyncRelayCommand(() => LoadPhotosAsync(reset: true));
        ResetFiltersCommand = new AsyncRelayCommand(ResetFiltersAsync);
        LoadMoreCommand = new AsyncRelayCommand(() => LoadPhotosAsync(reset: false), () => Photos.Count < _filteredTotal);
        ShowInExplorerCommand = new RelayCommand(ShowInExplorer,
            () => SelectedPhoto is not null && File.Exists(SelectedPhoto.FullPath));
        OpenPhotoCommand = new RelayCommand(OpenPhoto,
            () => SelectedPhoto is not null && File.Exists(SelectedPhoto.FullPath));
        EditPhotoCaptureDateCommand = new AsyncRelayCommand(EditSelectedPhotoCaptureDateAsync, CanEditSelectedPhotoDate);
        ResetPhotoCaptureDateCommand = new AsyncRelayCommand(ResetSelectedPhotoCaptureDateAsync,
            () => CanEditSelectedPhotoDate() && SelectedPhoto?.IsManualCaptureDate == true);

        FindExactDuplicatesCommand = new AsyncRelayCommand(FindExactDuplicatesAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        PauseDuplicateAnalysisCommand = new RelayCommand(() =>
        {
            _duplicateAnalyzer.Pause();
            StatusText = "Поиск точных дублей приостановлен.";
            RaiseDuplicateCommands();
        }, () => IsDuplicateAnalysisRunning && !_duplicateAnalyzer.IsPaused);
        ResumeDuplicateAnalysisCommand = new RelayCommand(() =>
        {
            _duplicateAnalyzer.Resume();
            StatusText = "Поиск точных дублей продолжен.";
            RaiseDuplicateCommands();
        }, () => IsDuplicateAnalysisRunning && _duplicateAnalyzer.IsPaused);
        StopDuplicateAnalysisCommand = new RelayCommand(() => _duplicateAnalyzer.Stop(), () => IsDuplicateAnalysisRunning);
        RefreshDuplicatesCommand = new AsyncRelayCommand(LoadDuplicateGroupsAsync, () => !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        ShowDuplicateInExplorerCommand = new RelayCommand(ShowDuplicateInExplorer,
            () => SelectedDuplicateFile is not null && File.Exists(SelectedDuplicateFile.FullPath));
        OpenDuplicateFileCommand = new RelayCommand(OpenDuplicateFile,
            () => SelectedDuplicateFile is not null && File.Exists(SelectedDuplicateFile.FullPath));
        QuarantineOtherCopiesCommand = new AsyncRelayCommand(QuarantineOtherCopiesAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedDuplicateGroup is not null && SelectedDuplicateGroup.Files.Count > 1 && SelectedDuplicateFile is not null);
        ShowDuplicateFolderInExplorerCommand = new RelayCommand(ShowSelectedDuplicateFolderInExplorer,
            () => SelectedDuplicateFolderIntersection is not null && Directory.Exists(SelectedDuplicateFolderIntersection.FolderAPath));
        ShowDuplicateFolderMatchInExplorerCommand = new RelayCommand(ShowSelectedDuplicateFolderMatchInExplorer,
            () => SelectedDuplicateFolderIntersection is not null && Directory.Exists(SelectedDuplicateFolderIntersection.FolderBPath));
        QuarantineDuplicateFolderPairSideCommand = new AsyncRelayCommand(QuarantineSelectedDuplicateFolderPairSideAsync,
            () => CanPerformCatalogReview && SelectedDuplicateFolderIntersection is not null && DuplicateFolderPairRows.Count > 0);

        FindVisualDuplicatesCommand = new AsyncRelayCommand(FindVisualDuplicatesAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        RefreshVisualDuplicatesCommand = new AsyncRelayCommand(LoadVisualDuplicateGroupsAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        PauseVisualAnalysisCommand = new RelayCommand(() =>
        {
            _perceptualAnalyzer.Pause();
            StatusText = "Поиск визуальных дублей приостановлен.";
            RaiseVisualCommands();
        }, () => IsVisualAnalysisRunning && !_perceptualAnalyzer.IsPaused);
        ResumeVisualAnalysisCommand = new RelayCommand(() =>
        {
            _perceptualAnalyzer.Resume();
            StatusText = "Поиск визуальных дублей продолжен.";
            RaiseVisualCommands();
        }, () => IsVisualAnalysisRunning && _perceptualAnalyzer.IsPaused);
        StopVisualAnalysisCommand = new RelayCommand(() => _perceptualAnalyzer.Stop(), () => IsVisualAnalysisRunning);
        ShowVisualInExplorerCommand = new RelayCommand(ShowVisualInExplorer,
            () => SelectedVisualDuplicateFile is not null && File.Exists(SelectedVisualDuplicateFile.FullPath));
        OpenVisualFileCommand = new RelayCommand(OpenVisualFile,
            () => SelectedVisualDuplicateFile is not null && File.Exists(SelectedVisualDuplicateFile.FullPath));

        AnalyzeSelectedQualityCommand = new AsyncRelayCommand(AnalyzeSelectedQualityAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedVisualDuplicateGroup is not null && SelectedVisualDuplicateGroup.Files.Count > 0);
        PauseQualityAnalysisCommand = new RelayCommand(() =>
        {
            _qualityAnalyzer.Pause();
            StatusText = "Оценка качества приостановлена.";
            RaiseVisualCommands();
        }, () => IsQualityAnalysisRunning && !_qualityAnalyzer.IsPaused);
        ResumeQualityAnalysisCommand = new RelayCommand(() =>
        {
            _qualityAnalyzer.Resume();
            StatusText = "Оценка качества продолжена.";
            RaiseVisualCommands();
        }, () => IsQualityAnalysisRunning && _qualityAnalyzer.IsPaused);
        StopQualityAnalysisCommand = new RelayCommand(() => _qualityAnalyzer.Stop(), () => IsQualityAnalysisRunning);
        ClearVisualReviewCommand = new RelayCommand(ClearVisualReview,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedVisualDuplicateGroup is not null);
        QuarantineMarkedVisualCommand = new AsyncRelayCommand(QuarantineMarkedVisualAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedVisualDuplicateGroup is not null);

        FindBurstsCommand = new AsyncRelayCommand(FindBurstsAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        RefreshBurstsCommand = new AsyncRelayCommand(LoadBurstGroupsAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        PauseBurstAnalysisCommand = new RelayCommand(() =>
        {
            _burstAnalyzer.Pause();
            StatusText = "Поиск серий приостановлен.";
            RaiseBurstCommands();
        }, () => IsBurstAnalysisRunning && !_burstAnalyzer.IsPaused);
        ResumeBurstAnalysisCommand = new RelayCommand(() =>
        {
            _burstAnalyzer.Resume();
            StatusText = "Поиск серий продолжен.";
            RaiseBurstCommands();
        }, () => IsBurstAnalysisRunning && _burstAnalyzer.IsPaused);
        StopBurstAnalysisCommand = new RelayCommand(() => _burstAnalyzer.Stop(), () => IsBurstAnalysisRunning);
        ShowBurstInExplorerCommand = new RelayCommand(ShowBurstInExplorer,
            () => SelectedBurstFile is not null && File.Exists(SelectedBurstFile.FullPath));
        OpenBurstFileCommand = new RelayCommand(OpenBurstFile,
            () => SelectedBurstFile is not null && File.Exists(SelectedBurstFile.FullPath));
        AnalyzeSelectedBurstQualityCommand = new AsyncRelayCommand(AnalyzeSelectedBurstQualityAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedBurstGroup is not null && SelectedBurstGroup.Files.Count > 0);
        ApplyBurstRecommendationCommand = new RelayCommand(ApplyBurstRecommendationToQuarantineMarks,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedBurstGroup?.CanRecommend == true);
        MarkBurstOthersCommand = new RelayCommand(MarkBurstOthersForQuarantine,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedBurstGroup is not null && SelectedBurstFile is not null);
        ClearBurstReviewCommand = new RelayCommand(ClearBurstReview,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedBurstGroup is not null);
        QuarantineMarkedBurstCommand = new AsyncRelayCommand(QuarantineMarkedBurstAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning &&
                  SelectedBurstGroup is not null);

        AnalyzePeopleCommand = new AsyncRelayCommand(AnalyzePeopleAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        PausePeopleAnalysisCommand = new RelayCommand(() =>
        {
            _peopleAnalyzer.Pause();
            StatusText = "Индексация лиц приостановлена.";
            RaisePeopleCommands();
        }, () => IsPeopleAnalysisRunning && !_peopleAnalyzer.IsPaused);
        ResumePeopleAnalysisCommand = new RelayCommand(() =>
        {
            _peopleAnalyzer.Resume();
            StatusText = "Индексация лиц продолжена.";
            RaisePeopleCommands();
        }, () => IsPeopleAnalysisRunning && _peopleAnalyzer.IsPaused);
        StopPeopleAnalysisCommand = new RelayCommand(() => _peopleAnalyzer.Stop(), () => IsPeopleAnalysisRunning);
        GroupPeopleCommand = new AsyncRelayCommand(GroupPeopleAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        RefreshPeopleCommand = new AsyncRelayCommand(LoadPeopleAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        RenamePersonCommand = new AsyncRelayCommand(RenameSelectedPersonAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && HasSingleSelectedRealPersonGroup());
        AssignFaceToPersonCommand = new AsyncRelayCommand(AssignSelectedFaceToPersonAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedPersonFace is not null && PersonGroups.Any(x => x.Id > 0 && x.Id != SelectedPersonFace.PersonId));
        MergePersonGroupCommand = new AsyncRelayCommand(MergeSelectedPersonGroupAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && CanMergeCurrentPersonSelection());
        DeletePersonGroupCommand = new AsyncRelayCommand(DeleteSelectedPersonGroupAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && CanDeleteCurrentPersonSelection());
        RemoveFaceFromPersonCommand = new AsyncRelayCommand(RemoveSelectedFaceFromPersonAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedPersonFace?.PersonId is not null);
        IgnoreFaceCommand = new AsyncRelayCommand(IgnoreSelectedFaceAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedPersonFace is not null);
        IgnorePersonGroupCommand = new AsyncRelayCommand(IgnoreSelectedPersonGroupAsync,
            () => CanPerformCatalogReview && CanIgnoreCurrentPersonSelection());
        UndoLastFaceIgnoreCommand = new AsyncRelayCommand(UndoLastFaceIgnoreAsync,
            () => CanPerformCatalogReview && LatestFaceIgnoreAction?.IsActive == true);
        ShowPersonPhotosCommand = new AsyncRelayCommand(ShowSelectedPersonPhotosAsync,
            () => !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && HasSingleSelectedRealPersonGroup());
        ShowPersonFaceInExplorerCommand = new RelayCommand(ShowPersonFaceInExplorer,
            () => SelectedPersonFace is not null && File.Exists(SelectedPersonFace.FullPath));
        OpenPersonPhotoCommand = new RelayCommand(OpenPersonPhoto,
            () => SelectedPersonFace is not null && File.Exists(SelectedPersonFace.FullPath));
        SetPersonCoverCommand = new AsyncRelayCommand(SetSelectedPersonCoverAsync,
            () => CanPerformCatalogReview && HasSingleSelectedRealPersonGroup() && SelectedPersonGroup is { Id: > 0 } && SelectedPersonFace?.PersonId == SelectedPersonGroup.Id);

        AnalyzeEventsCommand = new AsyncRelayCommand(AnalyzeEventsAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning);
        PauseEventAnalysisCommand = new RelayCommand(() =>
        {
            _eventAnalyzer.Pause();
            StatusText = "Анализ событий приостановлен.";
            RaiseEventCommands();
        }, () => IsEventAnalysisRunning && !_eventAnalyzer.IsPaused);
        ResumeEventAnalysisCommand = new RelayCommand(() =>
        {
            _eventAnalyzer.Resume();
            StatusText = "Анализ событий продолжен.";
            RaiseEventCommands();
        }, () => IsEventAnalysisRunning && _eventAnalyzer.IsPaused);
        StopEventAnalysisCommand = new RelayCommand(() => _eventAnalyzer.Stop(), () => IsEventAnalysisRunning);
        RefreshEventsCommand = new AsyncRelayCommand(() => LoadEventsAsync(),
            () => !IsEventAnalysisRunning && !IsFileOperationRunning);
        RenameEventCommand = new AsyncRelayCommand(RenameSelectedEventAsync,
            () => !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedEventGroup is { Id: > 0 });
        MergeEventCommand = new AsyncRelayCommand(MergeSelectedEventAsync,
            () => !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedEventGroup is { Id: > 0 } && EventGroups.Count > 1);
        ShowEventPhotosCommand = new AsyncRelayCommand(ShowSelectedEventPhotosAsync,
            () => !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedEventGroup is { Id: > 0 });
        ShowEventPhotoInExplorerCommand = new RelayCommand(ShowEventPhotoInExplorer,
            () => SelectedEventPhoto is not null && File.Exists(SelectedEventPhoto.FullPath));
        OpenEventPhotoCommand = new RelayCommand(OpenEventPhoto,
            () => SelectedEventPhoto is not null && File.Exists(SelectedEventPhoto.FullPath));
        SetEventCoverCommand = new AsyncRelayCommand(SetSelectedEventCoverAsync,
            () => CanPerformCatalogReview && SelectedEventGroup is { Id: > 0 } && SelectedEventPhoto is not null);
        EditEventNotesCommand = new AsyncRelayCommand(EditSelectedEventNotesAsync,
            () => CanPerformCatalogReview && SelectedEventGroup is { Id: > 0 });
        SplitEventCommand = new AsyncRelayCommand(SplitSelectedEventAsync,
            () => CanPerformCatalogReview && SelectedEventGroup is { Id: > 0, PhotoCount: > 1 } && SelectedEventPhoto is not null);

        BuildOrganizationPlanCommand = new AsyncRelayCommand(BuildOrganizationPlanAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && !string.IsNullOrWhiteSpace(OrganizationDestinationRoot));
        SelectTrustedOrganizationCommand = new RelayCommand(SelectTrustedOrganizationRows,
            () => !IsFileOperationRunning && OrganizationPlan.Any(x => x.IsReady && x.HasTrustedDate));
        ClearOrganizationSelectionCommand = new RelayCommand(ClearOrganizationSelection,
            () => !IsFileOperationRunning && OrganizationPlan.Any(x => x.IsSelected));
        ExecuteOrganizationPlanCommand = new AsyncRelayCommand(ExecuteOrganizationPlanAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && OrganizationPlan.Any(x => x.IsSelected && x.IsReady));
        PauseOrganizationCommand = new RelayCommand(() =>
        {
            _organizationService.Pause();
            StatusText = "Организация архива приостановлена после текущего файла.";
            RaiseOrganizationCommands();
        }, () => IsOrganizationMoveRunning && !_organizationService.IsPaused);
        ResumeOrganizationCommand = new RelayCommand(() =>
        {
            _organizationService.Resume();
            StatusText = "Организация архива продолжена.";
            RaiseOrganizationCommands();
        }, () => IsOrganizationMoveRunning && _organizationService.IsPaused);
        StopOrganizationCommand = new RelayCommand(() => _organizationService.Stop(), () => IsOrganizationMoveRunning);
        RefreshOrganizationHistoryCommand = new AsyncRelayCommand(LoadOrganizationHistoryAsync, () => !IsFileOperationRunning);
        UndoOrganizationMoveCommand = new AsyncRelayCommand(UndoSelectedOrganizationMoveAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedOrganizationMove?.IsActive == true);
        ShowOrganizationSourceCommand = new RelayCommand(() => { if (SelectedOrganizationPlanItem is not null) ShowPathInExplorer(SelectedOrganizationPlanItem.SourcePath); },
            () => SelectedOrganizationPlanItem is not null && File.Exists(SelectedOrganizationPlanItem.SourcePath));
        ShowOrganizationTargetCommand = new RelayCommand(() =>
        {
            if (SelectedOrganizationPlanItem is null) return;
            var path = File.Exists(SelectedOrganizationPlanItem.TargetPath) ? SelectedOrganizationPlanItem.TargetPath : Path.GetDirectoryName(SelectedOrganizationPlanItem.TargetPath);
            if (!string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path))) Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }, () => SelectedOrganizationPlanItem is not null);
        ShowOrganizationMoveCommand = new RelayCommand(() =>
        {
            if (SelectedOrganizationMove is null) return;
            var path = SelectedOrganizationMove.IsActive ? SelectedOrganizationMove.NewPath : SelectedOrganizationMove.OriginalPath;
            if (File.Exists(path)) ShowPathInExplorer(path);
        }, () => SelectedOrganizationMove is not null && File.Exists(SelectedOrganizationMove.IsActive ? SelectedOrganizationMove.NewPath : SelectedOrganizationMove.OriginalPath));

        RefreshQuarantineCommand = new AsyncRelayCommand(LoadQuarantineAsync, () => !IsEventAnalysisRunning && !IsFileOperationRunning);
        UndoQuarantineCommand = new AsyncRelayCommand(UndoSelectedQuarantineAsync,
            () => !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedQuarantineAction?.IsActive == true);
        PermanentDeleteQuarantineCommand = new AsyncRelayCommand(PermanentDeleteSelectedQuarantineAsync,
            () => AllowPermanentDelete && !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning && !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning && !IsFileOperationRunning && SelectedQuarantineAction?.IsActive == true);
        ShowQuarantineInExplorerCommand = new RelayCommand(ShowQuarantineInExplorer,
            () => SelectedQuarantineAction is not null && File.Exists(SelectedQuarantineAction.CurrentPath));
        OpenQuarantineFileCommand = new RelayCommand(OpenQuarantineFile,
            () => SelectedQuarantineAction is not null && File.Exists(SelectedQuarantineAction.CurrentPath));
    }

    public async Task ApplyBatchCaptureDateAsync(IReadOnlyCollection<PhotoItem> photos, DateTime captureDate, bool preserveExistingTime)
    {
        if (!CanMoveEventPhotos) throw new InvalidOperationException("Дождитесь окончания текущего анализа или файловой операции.");
        var items = photos?.Where(x => x.Id > 0).GroupBy(x => x.Id).Select(x => x.First()).ToArray() ?? [];
        if (items.Length == 0) throw new ArgumentException("Не выбраны фотографии.");
        IsFileOperationRunning = true;
        try
        {
            var updated = await _database.SetManualCaptureDatesAsync(items.Select(x => x.Id).ToArray(), captureDate, preserveExistingTime);
            StatusText = preserveExistingTime
                ? $"Для {updated:N0} фото изменена календарная дата с сохранением времени каждого кадра. Только каталог PAM."
                : $"Для {updated:N0} фото назначена точная дата/время. Только каталог PAM.";
            await LoadFilterValuesAsync();
            await LoadPhotosAsync(reset: true, preferredPhotoId: items[0].Id);
            await LoadEventsAsync();
            OrganizationPlan.Clear();
            OrganizationSummaryText = "Пакетно изменены даты в каталоге — preview организации нужно построить заново.";
            RaiseOrganizationCommands();
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    public async Task AssignPhotosToEventAsync(IReadOnlyCollection<PhotoItem> photos, long targetEventId)
    {
        if (!CanMoveEventPhotos) throw new InvalidOperationException("Дождитесь окончания текущего анализа или файловой операции.");
        var items = photos?.Where(x => x.Id > 0).GroupBy(x => x.Id).Select(x => x.First()).ToArray() ?? [];
        if (items.Length == 0) throw new ArgumentException("Не выбраны фотографии.");
        var target = EventGroups.FirstOrDefault(x => x.Id == targetEventId) ?? throw new InvalidOperationException("Выбранное событие больше не существует.");
        IsFileOperationRunning = true;
        try
        {
            var result = await _database.AssignFilesToEventAsync(targetEventId, items.Select(x => x.Id).ToArray());
            if (result.Assigned <= 0) throw new InvalidOperationException("Выбранные фотографии больше не доступны в каталоге.");
            StatusText = $"Назначено событию «{target.DisplayName}»: {result.Assigned:N0} фото. Файлы на диске не изменялись." +
                         (result.SourceEventsDeleted > 0 ? $" Пустых событий удалено из каталога: {result.SourceEventsDeleted:N0}." : "");
            await LoadEventsAsync(targetEventId);
            OrganizationPlan.Clear();
            OrganizationSummaryText = "Назначения событий изменились — preview организации нужно построить заново.";
            RaiseOrganizationCommands();
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    public async Task ExportCatalogAsync(string path, bool json)
    {
        if (IsFileOperationRunning) throw new InvalidOperationException("Дождитесь окончания текущей операции.");
        IsFileOperationRunning = true;
        try
        {
            StatusText = "Экспорт каталога и метаданных…";
            var count = await new CatalogExportService(_database).ExportAsync(path, json);
            StatusText = $"Экспортировано записей: {count:N0} → {path}";
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    public async Task InitializeAsync() => await RefreshAllAsync();

    public async Task AddSourceFolderAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        await _database.AddSourceFolderAsync(path);
        await LoadSourcesAsync();
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath);
        var normalizedPath = string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        SelectedSource = Sources.FirstOrDefault(x => string.Equals(x.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
        StatusText = "Папка добавлена. Нажмите «Сканировать».";
        InvalidateOrganizationPlan("Список источников изменился — постройте preview организации заново.");
        RaiseScanCommands();
    }

    public async Task RemoveSelectedSourceAsync(bool removeCatalogRecords)
    {
        if (!CanManageSelectedSource || SelectedSource is null) return;
        var source = SelectedSource;
        IsFileOperationRunning = true;
        try
        {
            if (!removeCatalogRecords)
            {
                await _database.RemoveSourceFolderOnlyAsync(source.Path);
                StatusText = "Источник убран из списка. Фотографии на диске и записи каталога PAM не изменены.";
            }
            else
            {
                var result = await _database.RemoveSourceFolderAndCatalogAsync(source.Path);

                foreach (var cacheFile in result.CacheFilesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!CacheFileSafety.TryDeleteGeneratedCacheFile(cacheFile, out var cacheDeleteError))
                        LoggingService.Warn("Не удалён файл кэша после удаления источника: " + cacheFile + " — " + cacheDeleteError);
                }

                var quarantineSuffix = result.ActiveQuarantineCount > 0
                    ? $" В карантине оставлено {result.ActiveQuarantineCount:N0} файлов этого источника; их можно восстановить или удалить позже через «Карантин / Undo»."
                    : "";
                StatusText = result.AuditRecordsRetained > 0
                    ? $"Источник убран. Из активного каталога удалено {result.DeletedCatalogRecords:N0} записей; {result.AuditRecordsRetained:N0} скрытых строк сохранено только для Undo/аудита. Файлы на диске не тронуты.{quarantineSuffix}"
                    : $"Источник убран. Из каталога PAM удалено {result.DeletedCatalogRecords:N0} записей. Файлы на диске не тронуты.{quarantineSuffix}";
            }

            SelectedSource = null;
            ClearVisualGroupsAfterLibraryChange();
            await RefreshAllAsync();
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    public async Task ReplaceSelectedSourceAsync(string newPath)
    {
        if (!CanManageSelectedSource || SelectedSource is null) return;
        if (!Directory.Exists(newPath)) throw new DirectoryNotFoundException("Новая папка не существует: " + newPath);
        var old = SelectedSource;
        IsFileOperationRunning = true;
        try
        {
            var changed = await _database.ReplaceSourceFolderPathAsync(old.Path, newPath);
            StatusText = $"Путь источника заменён. Сохранены анализы для {changed:N0} проиндексированных файлов. Нажмите «Сканировать», чтобы проверить новое расположение.";
            await LoadSourcesAsync();
            var full = Path.GetFullPath(newPath);
            var root = Path.GetPathRoot(full);
            var normalized = string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            SelectedSource = Sources.FirstOrDefault(x => string.Equals(x.Path, normalized, StringComparison.OrdinalIgnoreCase));
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            InvalidateOrganizationPlan("Путь источника изменился — постройте preview организации заново.");
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    public void RequestStop()
    {
        _scanner.Stop();
        _duplicateAnalyzer.Stop();
        _perceptualAnalyzer.Stop();
        _qualityAnalyzer.Stop();
        _burstAnalyzer.Stop();
        _peopleAnalyzer.Stop();
        _eventAnalyzer.Stop();
        _organizationService.Stop();
    }

    private async Task StartScanAsync()
    {
        if (Sources.Count == 0) return;
        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 95));
            ScanCountersText = p.Total > 0
                ? $"{p.Processed:N0}/{p.Total:N0}  новых/изменённых: {p.Indexed:N0}  без изменений: {p.Skipped:N0}  ошибок: {p.Errors:N0}"
                : $"Найдено: {p.Total:N0}";
            RaiseScanCommands();
        });

        try
        {
            var paths = Sources.Select(x => x.Path).ToList();
            await _scanner.ScanAsync(paths, progress, _scanCts.Token);
            StatusText = _scanner.HadIncompleteTraversal
                ? "Сканирование завершено с предупреждением: один из источников был прочитан не полностью. Статус отсутствующих файлов для него не менялся."
                : "Сканирование завершено.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Сканирование остановлено. Уже проиндексированные файлы сохранены; следующий запуск продолжит с изменившихся файлов.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Scan failed", ex);
            StatusText = "Ошибка сканирования: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка сканирования", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            IsScanning = false;
            ClearVisualGroupsAfterLibraryChange();
            await RefreshAllAsync();
        }
    }

    private async Task FindExactDuplicatesAsync()
    {
        IsDuplicateAnalysisRunning = true;
        DuplicateProgressText = "Подготовка…";

        var progress = new Progress<ExactDuplicateProgress>(p =>
        {
            var percent = p.TotalBytes > 0 ? Math.Clamp(p.ProcessedBytes * 100.0 / p.TotalBytes, 0, 100) : 0;
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 90));
            DuplicateProgressText = p.CandidateFiles == 0
                ? p.Stage
                : $"{p.ProcessedFiles:N0}/{p.CandidateFiles:N0} файлов · {percent:0.0}% по объёму · " +
                  $"посчитано: {p.HashedFiles:N0} · из кэша: {p.CachedFiles:N0} · ошибок: {p.ErrorFiles:N0}";
            RaiseDuplicateCommands();
        });

        try
        {
            await _duplicateAnalyzer.AnalyzeAsync(progress, CancellationToken.None);
            StatusText = "Поиск точных дублей завершён.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Поиск точных дублей остановлен. Уже рассчитанные SHA-256 сохранены и будут использованы при следующем запуске.";
            DuplicateProgressText += " · остановлено пользователем";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Exact duplicate analysis failed", ex);
            StatusText = "Ошибка поиска дублей: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка поиска точных дублей", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsDuplicateAnalysisRunning = false;
            await LoadDuplicateGroupsAsync();
            await LoadStatisticsAsync();
        }
    }

    private async Task FindVisualDuplicatesAsync()
    {
        IsVisualAnalysisRunning = true;
        VisualDuplicateProgressText = "Подготовка…";
        var progress = new Progress<VisualDuplicateProgress>(p =>
        {
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 90));
            VisualDuplicateProgressText = p.TotalFiles == 0
                ? p.Stage
                : $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · посчитано: {p.ComputedFiles:N0} · из кэша: {p.CachedFiles:N0} · ошибок: {p.ErrorFiles:N0}" +
                  (p.GroupsFound > 0 ? $" · групп: {p.GroupsFound:N0}" : "");
            RaiseVisualCommands();
        });

        try
        {
            var groups = await _perceptualAnalyzer.AnalyzeAsync(SelectedVisualThreshold, progress);
            ApplyVisualGroups(groups);
            StatusText = $"Анализ визуальных дублей завершён. Групп: {groups.Count:N0}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Поиск визуальных дублей остановлен. Уже рассчитанные отпечатки сохранены.";
            VisualDuplicateProgressText += " · остановлено пользователем";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Visual duplicate analysis failed", ex);
            StatusText = "Ошибка визуального анализа: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка поиска визуальных дублей", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsVisualAnalysisRunning = false;
        }
    }

    private async Task AnalyzeSelectedQualityAsync()
    {
        var group = SelectedVisualDuplicateGroup;
        if (group is null || group.Files.Count == 0) return;

        IsQualityAnalysisRunning = true;
        QualityProgressText = "Подготовка оценки качества…";
        var groupKey = group.GroupKey;
        var selectedFileId = SelectedVisualDuplicateFile?.Id;
        var progress = new Progress<QualityProgress>(p =>
        {
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 90));
            QualityProgressText = p.TotalFiles == 0
                ? p.Stage
                : $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · посчитано: {p.ComputedFiles:N0} · из кэша: {p.CachedFiles:N0} · ошибок: {p.ErrorFiles:N0}";
            RaiseVisualCommands();
        });

        try
        {
            await _qualityAnalyzer.AnalyzeGroupAsync(group.Files, progress);
            var groups = await _perceptualAnalyzer.LoadCachedGroupsAsync(SelectedVisualThreshold);
            ApplyVisualGroups(groups, groupKey, selectedFileId);
            var refreshed = SelectedVisualDuplicateGroup;
            StatusText = refreshed?.RecommendedFile is not null
                ? $"Оценка завершена. Рекомендуемый кадр: {refreshed.RecommendedFile.FileName} ({refreshed.RecommendedFile.QualityDisplay})."
                : "Оценка качества завершена.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Оценка качества остановлена. Уже рассчитанные результаты сохранены.";
            QualityProgressText += " · остановлено пользователем";
            var groups = await _perceptualAnalyzer.LoadCachedGroupsAsync(SelectedVisualThreshold);
            ApplyVisualGroups(groups, groupKey, selectedFileId);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Quality analysis failed", ex);
            StatusText = "Ошибка оценки качества: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка оценки качества", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsQualityAnalysisRunning = false;
        }
    }

    private void ClearVisualReview()
    {
        var group = SelectedVisualDuplicateGroup;
        if (group is null) return;
        foreach (var file in group.Files)
            file.IsMarkedForQuarantine = false;
        StatusText = "Отметки «в карантин» текущей визуальной группы очищены.";
        RaiseVisualCommands();
    }

    private async Task QuarantineMarkedVisualAsync()
    {
        var group = SelectedVisualDuplicateGroup;
        if (group is null) return;

        var marked = group.Files.Where(x => x.IsMarkedForQuarantine).ToList();
        if (marked.Count == 0)
        {
            MessageBox.Show("Отметьте галочками конкретные файлы, которые хотите отправить в карантин.", "Ничего не отмечено", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var keepers = group.Files.Where(x => !x.IsMarkedForQuarantine).ToList();
        if (keepers.Count == 0)
        {
            MessageBox.Show("Нельзя отправить в карантин всю группу. Снимите галочку «в карантин» хотя бы с одного файла — неотмеченные файлы остаются на месте.", "Нужно оставить хотя бы один файл", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bytes = marked.Sum(x => x.FileSize);
        var minSimilarity = marked.Min(x => x.SimilarityPercent);
        var answer = MessageBox.Show(
            $"Останется на месте: {keepers.Count:N0} файл(ов).\n" +
            $"В карантин будет перемещено: {marked.Count:N0} файлов ({ByteFormatter.Format(bytes)}).\n" +
            $"Минимальное сходство с представителем группы: {minSimilarity:0.0}%.\n\n" +
            "ВАЖНО: это визуально похожие кадры, а НЕ доказанные SHA-256 дубли. " +
            "PAM переместит только отмеченные галочкой «в карантин» файлы; все неотмеченные останутся. Карантин обратим через Undo.\n\nПродолжить?",
            "Подтвердить карантин визуальных копий",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsFileOperationRunning = true;
        try
        {
            StatusText = "Проверка файлов и перенос отмеченных визуальных копий в карантин…";
            var result = await _quarantineService.QuarantineSelectedVisualAsync(group, marked);
            StatusText = result.Failed == 0
                ? $"В карантин перемещено {result.Succeeded:N0} отмеченных файлов ({ByteFormatter.Format(result.BytesMoved)})."
                : $"Ручной карантин: успешно {result.Succeeded:N0}, ошибок {result.Failed:N0}.";

            if (result.Failed > 0)
            {
                var details = string.Join("\n", result.Errors.Take(8));
                if (result.Errors.Count > 8) details += $"\n…и ещё {result.Errors.Count - 8:N0}. Подробности есть в журнале.";
                MessageBox.Show(details, "Не все файлы удалось переместить", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Visual quarantine batch failed", ex);
            StatusText = "Ошибка ручного карантина: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка ручного карантина", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
            await LoadDuplicateGroupsAsync();
            await LoadQuarantineAsync();
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            await LoadEventsAsync();
            await LoadVisualDuplicateGroupsAsync();
        }
    }

    private async Task FindBurstsAsync()
    {
        IsBurstAnalysisRunning = true;
        BurstProgressText = "Подготовка…";
        var progress = new Progress<BurstProgress>(p =>
        {
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 90));
            BurstProgressText = p.TotalFiles == 0
                ? p.Stage
                : $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · отпечатков посчитано: {p.ComputedHashes:N0} · из кэша: {p.CachedHashes:N0} · ошибок: {p.ErrorFiles:N0}" +
                  (p.GroupsFound > 0 ? $" · серий: {p.GroupsFound:N0}" : "");
            RaiseBurstCommands();
        });

        try
        {
            var groups = await _burstAnalyzer.AnalyzeAsync(SelectedBurstMaxGap, SelectedBurstMinSize, SelectedBurstKeepCount, progress);
            ApplyBurstGroups(groups);
            StatusText = $"Поиск серий завершён. Найдено: {groups.Count:N0}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Поиск серий остановлен. Уже рассчитанные визуальные отпечатки сохранены.";
            BurstProgressText += " · остановлено пользователем";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Burst analysis failed", ex);
            StatusText = "Ошибка поиска серий: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка поиска серий", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBurstAnalysisRunning = false;
        }
    }

    private async Task LoadBurstGroupsAsync()
    {
        try
        {
            StatusText = "Группировка серий из уже рассчитанных отпечатков…";
            var groups = await _burstAnalyzer.LoadCachedGroupsAsync(SelectedBurstMaxGap, SelectedBurstMinSize, SelectedBurstKeepCount);
            ApplyBurstGroups(groups);
            StatusText = $"Серии обновлены из кэша. Найдено: {groups.Count:N0}.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Loading cached bursts failed", ex);
            StatusText = "Не удалось обновить серии: " + ex.Message;
        }
    }

    private async Task AnalyzeSelectedBurstQualityAsync()
    {
        var group = SelectedBurstGroup;
        if (group is null || group.Files.Count == 0) return;

        IsQualityAnalysisRunning = true;
        BurstQualityProgressText = "Подготовка оценки серии…";
        var groupKey = group.GroupKey;
        var selectedFileId = SelectedBurstFile?.Id;
        var progress = new Progress<QualityProgress>(p =>
        {
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 90));
            BurstQualityProgressText = p.TotalFiles == 0
                ? p.Stage
                : $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · посчитано: {p.ComputedFiles:N0} · из кэша: {p.CachedFiles:N0} · ошибок: {p.ErrorFiles:N0}";
            RaiseBurstCommands();
        });

        try
        {
            await _qualityAnalyzer.AnalyzeGroupAsync(group.Files, progress);
            var groups = await _burstAnalyzer.LoadCachedGroupsAsync(SelectedBurstMaxGap, SelectedBurstMinSize, SelectedBurstKeepCount);
            ApplyBurstGroups(groups, groupKey, selectedFileId);
            var refreshed = SelectedBurstGroup;
            if (refreshed?.CanRecommend == true)
            {
                var names = string.Join(", ", refreshed.Files.Where(x => x.IsRecommended).Select(x => x.FileName));
                StatusText = $"Оценка серии завершена. PAM рекомендует оставить {refreshed.RecommendedCount}: {names}.";
            }
            else
                StatusText = "Оценка серии завершена.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Оценка серии остановлена. Уже рассчитанные результаты сохранены.";
            BurstQualityProgressText += " · остановлено пользователем";
            var groups = await _burstAnalyzer.LoadCachedGroupsAsync(SelectedBurstMaxGap, SelectedBurstMinSize, SelectedBurstKeepCount);
            ApplyBurstGroups(groups, groupKey, selectedFileId);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Burst quality analysis failed", ex);
            StatusText = "Ошибка оценки серии: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка оценки серии", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsQualityAnalysisRunning = false;
        }
    }

    private void ApplyBurstRecommendationToQuarantineMarks()
    {
        var group = SelectedBurstGroup;
        if (group is null || !group.CanRecommend) return;
        group.ApplyRecommendations(SelectedBurstKeepCount);
        foreach (var file in group.Files)
            file.IsMarkedForQuarantine = !file.IsRecommended;
        var marked = group.Files.Count(x => x.IsMarkedForQuarantine);
        StatusText = $"Рекомендация применена: оставить {group.RecommendedCount:N0}, в карантин отмечено {marked:N0}. Ничего ещё не перемещено; галочки можно изменить вручную.";
        RaiseBurstCommands();
    }

    private void MarkBurstOthersForQuarantine()
    {
        var group = SelectedBurstGroup;
        var selected = SelectedBurstFile;
        if (group is null || selected is null) return;
        foreach (var file in group.Files)
            file.IsMarkedForQuarantine = file.Id != selected.Id;
        StatusText = $"Кадр {selected.FileName} оставлен без отметки; остальные {group.Files.Count - 1:N0} отмечены «в карантин». Файлы ещё не перемещались.";
        RaiseBurstCommands();
    }

    private void ClearBurstReview()
    {
        var group = SelectedBurstGroup;
        if (group is null) return;
        foreach (var file in group.Files)
            file.IsMarkedForQuarantine = false;
        StatusText = "Отметки «в карантин» выбранной серии очищены. Рекомендационные звёздочки оставлены как подсказка.";
        RaiseBurstCommands();
    }

    private async Task QuarantineMarkedBurstAsync()
    {
        var group = SelectedBurstGroup;
        if (group is null) return;

        var marked = group.Files.Where(x => x.IsMarkedForQuarantine).ToList();
        if (marked.Count == 0)
        {
            MessageBox.Show("Не отмечено ни одного кадра для карантина.", "Ничего не отмечено", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var keepers = group.Files.Where(x => !x.IsMarkedForQuarantine).ToList();
        if (keepers.Count == 0)
        {
            MessageBox.Show("Нельзя отправить в карантин всю серию. Снимите галочку «в карантин» хотя бы с одного кадра — все неотмеченные кадры остаются на месте.", "Нужно оставить хотя бы один кадр", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bytes = marked.Sum(x => x.FileSize);
        var answer = MessageBox.Show(
            $"Серия: {group.HeaderText}\n\n" +
            $"Останется на месте: {keepers.Count:N0} кадр(ов).\n" +
            $"В карантин переместятся отмеченные: {marked.Count:N0} ({ByteFormatter.Format(bytes)}).\n\n" +
            "ВАЖНО: кадры серии НЕ являются дублями. Рекомендация основана на времени, визуальной близости и техническом Quality Score. " +
            "PAM переместит только отмеченные галочкой «в карантин» кадры; все неотмеченные останутся. Карантин полностью обратим через Undo.\n\nПродолжить?",
            "Подтвердить карантин кадров серии",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsFileOperationRunning = true;
        try
        {
            StatusText = "Проверка и перенос отмеченных кадров серии в карантин…";
            var result = await _quarantineService.QuarantineSelectedBurstAsync(group, marked);
            StatusText = result.Failed == 0
                ? $"В карантин перемещено {result.Succeeded:N0} кадров серии ({ByteFormatter.Format(result.BytesMoved)})."
                : $"Карантин серии: успешно {result.Succeeded:N0}, ошибок {result.Failed:N0}.";

            if (result.Failed > 0)
            {
                var details = string.Join("\n", result.Errors.Take(8));
                if (result.Errors.Count > 8) details += $"\n…и ещё {result.Errors.Count - 8:N0}. Подробности есть в журнале.";
                MessageBox.Show(details, "Не все кадры удалось переместить", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Burst quarantine batch failed", ex);
            StatusText = "Ошибка карантина серии: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка карантина серии", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
            ClearVisualGroupsAfterLibraryChange();
            await LoadDuplicateGroupsAsync();
            await LoadQuarantineAsync();
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            await LoadEventsAsync();
            await LoadBurstGroupsAsync();
        }
    }

    private void ApplyBurstGroups(IReadOnlyList<BurstGroupItem> groups, string? preferredGroupKey = null, long? preferredFileId = null)
    {
        var previous = SelectedBurstGroup;
        var oldKey = preferredGroupKey ?? previous?.GroupKey;
        var oldFileId = preferredFileId ?? SelectedBurstFile?.Id;
        var quarantineState = previous is not null && previous.GroupKey == oldKey
            ? previous.Files.ToDictionary(x => x.Id, x => x.IsMarkedForQuarantine)
            : new Dictionary<long, bool>();

        BurstGroups.Clear();
        foreach (var group in groups)
        {
            group.ApplyRecommendations(SelectedBurstKeepCount);
            BurstGroups.Add(group);
        }

        SelectedBurstGroup = BurstGroups.FirstOrDefault(x => x.GroupKey == oldKey) ?? BurstGroups.FirstOrDefault();
        if (SelectedBurstGroup is not null)
        {
            foreach (var file in SelectedBurstGroup.Files)
            {
                if (quarantineState.TryGetValue(file.Id, out var isMarkedForQuarantine))
                    file.IsMarkedForQuarantine = isMarkedForQuarantine;
            }
            SelectedBurstGroup.NotifySummaryChanged();
        }
        if (SelectedBurstGroup is not null && oldFileId.HasValue)
            SelectedBurstFile = SelectedBurstGroup.Files.FirstOrDefault(x => x.Id == oldFileId.Value) ?? SelectedBurstGroup.Files.FirstOrDefault();

        var files = groups.Sum(x => x.FileCount);
        var rated = groups.Count(x => x.CanRecommend);
        BurstSummaryText = groups.Count == 0
            ? "Серии не найдены. Попробуйте увеличить максимальную паузу или уменьшить минимальное число кадров. Фото без достоверной даты в серии не включаются."
            : $"Серий: {groups.Count:N0} · кадров: {files:N0} · полностью оценено серий: {rated:N0} · пауза ≤ {SelectedBurstMaxGap} с · минимум {SelectedBurstMinSize} кадра(ов).";
        RaiseBurstCommands();
    }

    private async Task LoadVisualDuplicateGroupsAsync()
    {
        try
        {
            StatusText = "Группировка уже рассчитанных визуальных отпечатков…";
            var groups = await _perceptualAnalyzer.LoadCachedGroupsAsync(SelectedVisualThreshold);
            ApplyVisualGroups(groups);
            StatusText = $"Визуальные группы обновлены. Групп: {groups.Count:N0}.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Loading cached visual groups failed", ex);
            StatusText = "Не удалось обновить визуальные группы: " + ex.Message;
        }
    }

    private void ApplyVisualGroups(IReadOnlyList<VisualDuplicateGroupItem> groups, string? preferredGroupKey = null, long? preferredFileId = null)
    {
        var previousGroup = SelectedVisualDuplicateGroup;
        var oldKey = preferredGroupKey ?? previousGroup?.GroupKey;
        var oldFileId = preferredFileId ?? SelectedVisualDuplicateFile?.Id;
        var quarantineState = previousGroup is not null && previousGroup.GroupKey == oldKey
            ? previousGroup.Files.ToDictionary(x => x.Id, x => x.IsMarkedForQuarantine)
            : new Dictionary<long, bool>();

        VisualDuplicateGroups.Clear();
        foreach (var group in groups) VisualDuplicateGroups.Add(group);
        SelectedVisualDuplicateGroup = VisualDuplicateGroups.FirstOrDefault(x => x.GroupKey == oldKey) ?? VisualDuplicateGroups.FirstOrDefault();
        if (SelectedVisualDuplicateGroup is not null)
        {
            foreach (var file in SelectedVisualDuplicateGroup.Files)
            {
                if (quarantineState.TryGetValue(file.Id, out var isMarkedForQuarantine))
                    file.IsMarkedForQuarantine = isMarkedForQuarantine;
            }
        }
        if (SelectedVisualDuplicateGroup is not null && oldFileId.HasValue)
            SelectedVisualDuplicateFile = SelectedVisualDuplicateGroup.Files.FirstOrDefault(x => x.Id == oldFileId.Value) ?? SelectedVisualDuplicateGroup.Files.FirstOrDefault();

        var files = groups.Sum(x => x.FileCount);
        var ratedGroups = groups.Count(x => x.RatedCount == x.FileCount && x.FileCount > 0);
        VisualDuplicateSummaryText = groups.Count == 0
            ? "Группы не найдены. Можно увеличить порог до 6–7, но чем выше порог, тем больше ложных совпадений."
            : $"Групп: {groups.Count:N0} · файлов-кандидатов: {files:N0} · полностью оценено групп: {ratedGroups:N0} · порог dHash: {SelectedVisualThreshold}. Похожесть не является доказательством дубля.";
    }

    private async Task AnalyzePeopleAsync()
    {
        IsPeopleAnalysisRunning = true;
        PeopleProgressText = "Подготовка…";
        var progress = new Progress<FaceScanProgress>(p =>
        {
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 88));
            PeopleProgressText = p.TotalFiles == 0
                ? p.Stage
                : $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · обработано заново: {p.ComputedFiles:N0} · из кэша: {p.CachedFiles:N0} · лиц: {p.FacesFound:N0} · ошибок: {p.ErrorFiles:N0}";
            RaisePeopleCommands();
        });

        try
        {
            await _peopleAnalyzer.AnalyzeAsync(progress);
            StatusText = "Индексация лиц завершена. Теперь можно сгруппировать похожие лица.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Индексация лиц остановлена. Уже рассчитанные embeddings сохранены.";
            PeopleProgressText += " · остановлено пользователем";
        }
        catch (Exception ex)
        {
            LoggingService.Error("People analysis failed", ex);
            StatusText = "Ошибка индексации лиц: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка индексации лиц", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsPeopleAnalysisRunning = false;
            await LoadPeopleAsync();
        }
    }

    private async Task GroupPeopleAsync()
    {
        IsPeopleAnalysisRunning = true;
        try
        {
            PeopleProgressText = $"Группировка embeddings · порог {SelectedPeopleThreshold:0.00}…";
            StatusText = "Группировка лиц. Именованные пользователем группы не изменяются.";
            var result = await _peopleAnalyzer.GroupUnknownFacesAsync(SelectedPeopleThreshold, SelectedPeopleMinGroupSize);
            PeopleProgressText = $"Сгруппировано лиц: {result.FacesGrouped:N0} · групп: {result.GroupsCreated:N0} · осталось без группы: {result.FacesLeftUngrouped:N0}";
            StatusText = $"Группировка завершена. Создано групп: {result.GroupsCreated:N0}. Проверьте группы глазами и назовите нужных людей.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("People grouping failed", ex);
            StatusText = "Ошибка группировки лиц: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка группировки лиц", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsPeopleAnalysisRunning = false;
            await LoadPeopleAsync();
        }
    }

    private async Task LoadPeopleAsync()
    {
        var oldId = SelectedPersonGroup?.Id;
        var groups = await _database.GetPeopleGroupsAsync();
        PersonGroups.Clear();
        foreach (var group in groups) PersonGroups.Add(group);
        SelectedPersonGroup = PersonGroups.FirstOrDefault(x => x.Id == oldId) ?? PersonGroups.FirstOrDefault();

        var grouped = groups.Where(x => !x.IsUngrouped).Sum(x => x.FaceCount);
        var ungrouped = groups.FirstOrDefault(x => x.IsUngrouped)?.FaceCount ?? 0;
        var named = groups.Count(x => x.IsNamed);
        var auto = groups.Count(x => !x.IsUngrouped && !x.IsNamed);
        PeopleSummaryText = groups.Count == 0
            ? "Лица ещё не проиндексированы. Нажмите «1. Индексировать лица»."
            : $"Лиц в группах: {grouped:N0} · без группы: {ungrouped:N0} · именованных людей: {named:N0} · авто-групп: {auto:N0}. Авто-группа — только предположение; её нужно проверить глазами.";
        RaisePeopleCommands();
    }

    private async Task LoadSelectedPersonFacesAsync()
    {
        var group = SelectedPersonGroup;
        PersonFaces.Clear();
        SelectedPersonFace = null;
        if (group is null) return;

        try
        {
            var faces = await _database.GetFacesForPersonAsync(group.Id);
            // Selection may have changed while SQLite was being read.
            if (SelectedPersonGroup?.Id != group.Id) return;
            foreach (var face in faces) PersonFaces.Add(face);
            SelectedPersonFace = PersonFaces.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Loading person faces failed", ex);
            StatusText = "Не удалось загрузить лица группы: " + ex.Message;
        }
    }

    private async Task RenameSelectedPersonAsync()
    {
        var group = SelectedPersonGroup;
        if (group is null || group.Id <= 0) return;

        var dialog = new PersonNameWindow(group.IsNamed ? group.Name : "")
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.PersonName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Имя не может быть пустым. Для неназванной группы просто закройте окно.", "Имя человека", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _database.RenamePersonAsync(group.Id, name);
        StatusText = $"Группа названа: {name}.";
        await LoadPeopleAsync();
        SelectedPersonGroup = PersonGroups.FirstOrDefault(x => x.Id == group.Id) ?? SelectedPersonGroup;
    }

    private async Task SetSelectedPersonCoverAsync()
    {
        var group = SelectedPersonGroup;
        var face = SelectedPersonFace;
        if (group is null || group.Id <= 0 || face is null || face.PersonId != group.Id) return;
        await _database.SetPersonRepresentativeFaceAsync(group.Id, face.FaceId);
        StatusText = $"Обложка «{group.DisplayName}» изменена. Файлы не изменялись.";
        await LoadPeopleAsync();
        SelectedPersonGroup = PersonGroups.FirstOrDefault(x => x.Id == group.Id) ?? SelectedPersonGroup;
    }

    public async Task AssignFaceToPersonAsync(FaceItem face, long targetPersonId)
    {
        if (!CanPerformCatalogReview) throw new InvalidOperationException("Дождитесь окончания текущего анализа или файловой операции.");
        if (face is null || targetPersonId <= 0) throw new ArgumentException("Некорректная группа лиц.");
        if (face.PersonId == targetPersonId) return;
        var target = PersonGroups.FirstOrDefault(x => x.Id == targetPersonId);
        if (target is null) throw new InvalidOperationException("Целевая группа больше не существует.");
        await _database.AssignFaceToPersonAsync(face.FaceId, targetPersonId);
        StatusText = $"Лицо из «{face.FileName}» перенесено в группу «{target.DisplayName}». Фотография не изменена.";
        // Keep the source group selected after moving a face so the user can continue
        // cleaning that group. LoadPeopleAsync preserves the current group by Id when
        // it still contains faces; only an emptied/disappeared source falls back.
        await LoadPeopleAsync();
    }

    private async Task AssignSelectedFaceToPersonAsync()
    {
        var face = SelectedPersonFace;
        if (face is null) return;
        var targets = PersonGroups.Where(x => x.Id > 0 && x.Id != face.PersonId).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("Нет другой группы, куда можно назначить лицо.", "Люди", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PersonPickerWindow("Назначить выбранное лицо человеку:", targets)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true || dialog.SelectedGroup is null) return;
        var target = dialog.SelectedGroup;
        await _database.AssignFaceToPersonAsync(face.FaceId, target.Id);
        StatusText = $"Лицо назначено группе «{target.DisplayName}». Фотография не изменена.";
        // Stay in the source group after assigning the face elsewhere.
        await LoadPeopleAsync();
    }

    private async Task MergeSelectedPersonGroupAsync()
    {
        var selected = GetSelectedPersonGroups();
        var realGroups = selected.Where(x => x.Id > 0).ToList();
        if (realGroups.Count == 0) return;

        if (realGroups.Count >= 2)
        {
            var preferredTarget = GetPreferredPersonMergeTarget(realGroups);
            var dialog = new PersonPickerWindow(
                $"Объединить {realGroups.Count:N0} выбранных групп. В какую группу объединить остальные?\nИмя и обложка выбранной здесь группы сохранятся:",
                realGroups,
                preferredTarget?.Id)
            {
                Owner = Application.Current?.MainWindow
            };
            if (dialog.ShowDialog() != true || dialog.SelectedGroup is null) return;
            var target = dialog.SelectedGroup;
            var sources = realGroups.Where(x => x.Id != target.Id).ToList();
            var preview = string.Join("\n", realGroups.Take(8).Select(x => "• " + x.DisplayName));
            if (realGroups.Count > 8) preview += $"\n• …и ещё {realGroups.Count - 8:N0}";
            var answer = MessageBox.Show(
                $"Объединить выбранные группы в «{target.DisplayName}»?\n\n{preview}\n\n" +
                $"Будет перенесено лиц: {sources.Sum(x => x.FaceCount):N0}. Имя и обложка группы «{target.DisplayName}» сохранятся; остальные выбранные группы будут удалены из каталога PAM.\n\n" +
                "Исходные фотографии не изменяются.",
                "Объединить выбранные группы людей",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            await _database.MergePersonGroupsBatchAsync(realGroups.Select(x => x.Id), target.Id);
            _preferredPersonMergeTargetId = target.Id;
            StatusText = $"Объединено групп: {realGroups.Count:N0} → «{target.DisplayName}». Фотографии не изменены.";
            await LoadPeopleAsync();
            SelectedPersonGroup = PersonGroups.FirstOrDefault(x => x.Id == target.Id) ?? PersonGroups.FirstOrDefault();
            return;
        }

        var source = realGroups[0];
        var targets = PersonGroups.Where(x => x.Id > 0 && x.Id != source.Id).ToList();
        if (targets.Count == 0) return;

        var preferredSingleTarget = GetPreferredPersonMergeTarget(targets);
        var singleDialog = new PersonPickerWindow(
            $"В какую группу объединить «{source.DisplayName}»?\nИмя и обложка выбранной здесь группы сохранятся:",
            targets,
            preferredSingleTarget?.Id)
        {
            Owner = Application.Current?.MainWindow
        };
        if (singleDialog.ShowDialog() != true || singleDialog.SelectedGroup is null) return;
        var singleTarget = singleDialog.SelectedGroup;
        var singleAnswer = MessageBox.Show(
            $"Перенести все {source.FaceCount:N0} лиц из группы «{source.DisplayName}» в «{singleTarget.DisplayName}»?\n\nЭто меняет только локальный каталог лиц. Исходные фотографии не изменяются.",
            "Объединить группы людей",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (singleAnswer != MessageBoxResult.Yes) return;

        await _database.MergePersonGroupsAsync(source.Id, singleTarget.Id);
        _preferredPersonMergeTargetId = singleTarget.Id;
        StatusText = $"Группы объединены в «{singleTarget.DisplayName}». Фотографии не изменены.";
        await LoadPeopleAsync();
        SelectedPersonGroup = PersonGroups.FirstOrDefault(x => x.Id == singleTarget.Id) ?? PersonGroups.FirstOrDefault();
    }

    private async Task DeleteSelectedPersonGroupAsync()
    {
        var groups = GetSelectedPersonGroups().Where(x => x.Id > 0).ToList();
        if (groups.Count == 0) return;

        if (groups.Count == 1)
        {
            var group = groups[0];
            var namedWarning = group.IsNamed
                ? $"\n\nВНИМАНИЕ: имя «{group.Name}» тоже будет удалено из локального каталога."
                : "";
            var answer = MessageBox.Show(
                $"Расформировать группу «{group.DisplayName}»?\n\nВсе {group.FaceCount:N0} лиц этой группы вернутся в «Без группы» и снова смогут участвовать в автоматической группировке.{namedWarning}\n\nФотографии на диске НЕ удаляются, не перемещаются и не изменяются.",
                "Расформировать группу людей",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            var affected = await _database.DeletePersonGroupAsync(group.Id);
            StatusText = $"Группа «{group.DisplayName}» расформирована. {affected:N0} лиц возвращено в «Без группы» и снова участвует в группировке. Фотографии не изменены.";
        }
        else
        {
            var named = groups.Count(x => x.IsNamed);
            var preview = string.Join("\n", groups.Take(8).Select(x => "• " + x.DisplayName));
            if (groups.Count > 8) preview += $"\n• …и ещё {groups.Count - 8:N0}";
            var answer = MessageBox.Show(
                $"Расформировать {groups.Count:N0} выбранных групп?\n\n{preview}\n\n" +
                $"Лиц вернётся в «Без группы»: {groups.Sum(x => x.FaceCount):N0}. Именованных групп будет удалено: {named:N0}.\n\n" +
                "Фотографии на диске НЕ удаляются, не перемещаются и не изменяются.",
                "Расформировать выбранные группы людей",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var affected = await _database.DeletePersonGroupsAsync(groups.Select(x => x.Id));
            StatusText = $"Расформировано групп: {groups.Count:N0}. Лиц возвращено в «Без группы»: {affected:N0}. Фотографии не изменены.";
        }

        await LoadPeopleAsync();
        SelectedPersonGroup = PersonGroups.FirstOrDefault(x => x.Id == 0) ?? PersonGroups.FirstOrDefault();
    }

    private async Task RemoveSelectedFaceFromPersonAsync()
    {
        var face = SelectedPersonFace;
        var group = SelectedPersonGroup;
        if (face is null || group is null || !face.PersonId.HasValue) return;
        var answer = MessageBox.Show(
            $"Убрать это лицо из группы «{group.DisplayName}»?\n\nФотография не изменяется и никуда не перемещается. Лицо вернётся в «Без группы».",
            "Убрать лицо из группы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        await _database.RemoveFaceFromPersonAsync(face.FaceId);
        StatusText = "Лицо убрано из группы. Файл фотографии не изменён.";
        await LoadPeopleAsync();
        SelectedPersonGroup = PersonGroups.FirstOrDefault(x => x.Id == group.Id) ?? PersonGroups.FirstOrDefault(x => x.Id == 0) ?? PersonGroups.FirstOrDefault();
    }

    private async Task IgnoreSelectedFaceAsync()
    {
        var face = SelectedPersonFace;
        if (face is null) return;
        var answer = MessageBox.Show(
            "Пометить это обнаружение как «не лицо / не учитывать»?\n\nИсходная фотография не изменится. PAM сохранит служебную Undo-запись, поэтому последнее исключение можно восстановить кнопкой в разделе «Люди».",
            "Игнорировать обнаружение",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        await _database.IgnoreFaceWithUndoAsync(face.FaceId, "Одно лицо: " + face.FileName);
        LatestFaceIgnoreAction = await _database.GetLatestActiveFaceIgnoreActionAsync();
        StatusText = "Обнаружение исключено из группировки. Undo доступен. Фотография не изменена.";
        await LoadPeopleAsync();
    }

    private async Task IgnoreSelectedPersonGroupAsync()
    {
        var groups = GetSelectedPersonGroups().Where(x => x.FaceCount > 0).ToList();
        if (groups.Count == 0) return;

        if (groups.Count == 1)
        {
            var group = groups[0];
            var answer = MessageBox.Show(
                $"Исключить лица группы «{group.DisplayName}» из раздела «Люди» ({group.FaceCount:N0} лиц)?\n\n" +
                "Эти обнаружения перестанут показываться в разделе «Люди» и участвовать в автоматической группировке. " +
                "В отличие от команды «Расформировать группу», они НЕ вернутся в «Без группы».\n\n" +
                "Исходные фотографии не изменяются. PAM сохранит состав группы и прежнюю принадлежность каждого лица для Undo.",
                "Исключить лица группы",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var ignored = await _database.IgnorePersonGroupAsync(group.Id, group.DisplayName);
            LatestFaceIgnoreAction = await _database.GetLatestActiveFaceIgnoreActionAsync();
            StatusText = $"Из раздела «Люди» исключено {ignored:N0} обнаружений группы «{group.DisplayName}». Они больше не участвуют в группировке. Undo доступен; файлы не изменены.";
        }
        else
        {
            var preview = string.Join("\n", groups.Take(8).Select(x => "• " + x.DisplayName));
            if (groups.Count > 8) preview += $"\n• …и ещё {groups.Count - 8:N0}";
            var faceCount = groups.Sum(x => x.FaceCount);
            var answer = MessageBox.Show(
                $"Исключить все лица {groups.Count:N0} выбранных элементов из раздела «Люди»?\n\n{preview}\n\n" +
                $"Будет исключено до {faceCount:N0} обнаружений. Они перестанут показываться и участвовать в группировке.\n\n" +
                "Для каждой выбранной группы PAM сохранит отдельную Undo-запись, чтобы её имя/тип можно было восстановить корректно. Исходные фотографии не изменяются.",
                "Исключить выбранные группы",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var batch = await _database.IgnorePersonGroupsBatchAsync(
                groups.Select(x => (x.Id, $"Пакетное исключение: {x.DisplayName}")));
            LatestFaceIgnoreAction = await _database.GetLatestActiveFaceIgnoreActionAsync();
            StatusText = $"Из раздела «Люди» исключено обнаружений: {batch.FacesIgnored:N0} из {groups.Count:N0} выбранных элементов. Undo-записей: {batch.ActionsCreated:N0}; восстанавливаются по одной, начиная с последней. Файлы не изменены.";
        }

        await LoadPeopleAsync();
    }

    private async Task UndoLastFaceIgnoreAsync()
    {
        var action = LatestFaceIgnoreAction;
        if (action is null || !action.IsActive) return;
        var answer = MessageBox.Show(
            $"Восстановить последнее исключение?\n\n{action.ScopeLabel}\nЛиц: {action.FaceCount:N0}\n\n" +
            "PAM вернёт служебные записи лиц и их прежние группы, если соответствующие записи лиц ещё существуют. Фотографии не изменяются.",
            "Восстановить исключённые лица",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var restored = await _database.UndoFaceIgnoreActionAsync(action.Id);
        LatestFaceIgnoreAction = await _database.GetLatestActiveFaceIgnoreActionAsync();
        StatusText = $"Восстановлено обнаружений лиц: {restored:N0}. Файлы не изменены.";
        await LoadPeopleAsync();
    }

    private async Task ShowSelectedPersonPhotosAsync()
    {
        var group = SelectedPersonGroup;
        if (group is null || group.Id <= 0) return;
        SearchText = "";
        SelectedYear = "Все";
        SelectedCamera = "Все";
        SelectedReviewFilter = ReviewAll;
        SelectedSource = null;
        _eventFilterId = null;
        _eventFilterName = "";
        _personFilterId = group.Id;
        _personFilterName = group.DisplayName;
        SelectedMainTabIndex = 0;
        await LoadPhotosAsync(reset: true);
        StatusText = $"Библиотека отфильтрована по человеку: {group.DisplayName}. «Сбросить» уберёт этот фильтр.";
    }

    private void ShowPersonFaceInExplorer()
    {
        if (SelectedPersonFace is null) return;
        ShowPathInExplorer(SelectedPersonFace.FullPath);
    }

    private void OpenPersonPhoto()
    {
        if (SelectedPersonFace is null) return;
        OpenPath(SelectedPersonFace.FullPath);
    }

    private async Task AnalyzeEventsAsync()
    {
        IsEventAnalysisRunning = true;
        EventProgressText = "Подготовка…";
        var progress = new Progress<EventAnalysisProgress>(p =>
        {
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 90));
            if (p.TotalFiles > 0)
            {
                EventProgressText = $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · GPS прочитано: {p.GpsReadFiles:N0} · найдено GPS: {p.GpsFoundFiles:N0} · ошибок: {p.ErrorFiles:N0}" +
                                    (p.EventsFound > 0 ? $" · событий: {p.EventsFound:N0}" : "");
            }
            else
            {
                EventProgressText = p.Stage + (p.EventsFound > 0 ? $" · событий: {p.EventsFound:N0}" : "");
            }
            RaiseEventCommands();
        });

        try
        {
            var result = await _eventAnalyzer.AnalyzeAsync(SelectedEventMaxGap, SelectedEventMinSize, progress);
            StatusText = $"Анализ событий завершён. Создано автоматических событий: {result.EventsCreated:N0}.";
            EventProgressText = $"Событий: {result.EventsCreated:N0} · фото назначено: {result.PhotosAssigned:N0} · без надёжной даты: {result.PhotosWithoutReliableDate:N0} · в закреплённых событиях: {result.PhotosPreservedInManualEvents:N0}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Анализ событий остановлен. Уже считанные GPS-метаданные сохранены; старые события не заменены незавершённым результатом.";
            EventProgressText += " · остановлено пользователем";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Event analysis failed", ex);
            StatusText = "Ошибка анализа событий: " + ex.Message;
            MessageBox.Show(ex.Message, "Ошибка анализа событий", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEventAnalysisRunning = false;
            await LoadEventsAsync();
        }
    }

    private async Task LoadEventsAsync(long? preferredEventId = null)
    {
        var oldId = preferredEventId ?? SelectedEventGroup?.Id;
        var groups = await _database.GetEventGroupsAsync();
        EventGroups.Clear();
        foreach (var group in groups) EventGroups.Add(group);
        EventYears.Clear();
        foreach (var yearGroup in groups.GroupBy(x => x.StartDate.Year).OrderByDescending(x => x.Key))
        {
            EventYears.Add(new ArchiveYearItem
            {
                Year = yearGroup.Key,
                Events = yearGroup.OrderByDescending(x => x.StartDate).ToList()
            });
        }
        SelectedEventGroup = EventGroups.FirstOrDefault(x => x.Id == oldId) ?? EventGroups.FirstOrDefault();

        var photos = groups.Sum(x => x.PhotoCount);
        var manual = groups.Count(x => !x.IsAuto);
        var auto = groups.Count - manual;
        EventSummaryText = groups.Count == 0
            ? "Событий пока нет. Нажмите «Построить события». Автоанализ использует только надёжную дату: EXIF или явно исправленную вручную в каталоге; даты Windows fallback намеренно не склеиваются в события."
            : $"Событий: {groups.Count:N0} · автоматических: {auto:N0} · закреплённых пользователем: {manual:N0} · фото в событиях: {photos:N0}. Переименование, объединение и перенос фото закрепляют затронутые события и защищают их от следующей автоперегруппировки.";
        RaiseEventCommands();
    }

    private async Task LoadSelectedEventPhotosAsync()
    {
        var selected = SelectedEventGroup;
        EventPhotos.Clear();
        SelectedEventPhoto = null;
        if (selected is null) return;

        try
        {
            var items = await _database.QueryPhotosAsync(new PhotoQuery
            {
                EventId = selected.Id,
                Limit = 10000,
                Offset = 0
            });
            if (SelectedEventGroup?.Id != selected.Id) return;
            foreach (var item in items.OrderBy(x => x.CaptureDate)) EventPhotos.Add(item);
            SelectedEventPhoto = EventPhotos.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Loading event photos failed", ex);
            StatusText = "Не удалось загрузить фотографии события: " + ex.Message;
        }
    }

    private async Task RenameSelectedEventAsync()
    {
        var selected = SelectedEventGroup;
        if (selected is null || selected.Id <= 0) return;
        var dialog = new EventNameWindow(selected.Name)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.EventName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Название события не может быть пустым.", "Название события", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _database.RenameEventAsync(selected.Id, name);
        StatusText = $"Событие закреплено под названием «{name}». Файлы не изменены.";
        await LoadEventsAsync();
        SelectedEventGroup = EventGroups.FirstOrDefault(x => x.Id == selected.Id) ?? SelectedEventGroup;
    }

    private async Task SetSelectedEventCoverAsync()
    {
        var group = SelectedEventGroup;
        var photo = SelectedEventPhoto;
        if (group is null || group.Id <= 0 || photo is null) return;
        await _database.SetEventRepresentativePhotoAsync(group.Id, photo.Id);
        StatusText = $"Обложка события «{group.DisplayName}» изменена только в каталоге.";
        await LoadEventsAsync(group.Id);
    }

    private async Task EditSelectedEventNotesAsync()
    {
        var group = SelectedEventGroup;
        if (group is null || group.Id <= 0) return;
        var dialog = new EventNoteWindow(group.Notes) { Owner = Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true) return;
        await _database.SetEventNotesAsync(group.Id, dialog.Notes);
        StatusText = string.IsNullOrWhiteSpace(dialog.Notes)
            ? $"Заметка события «{group.DisplayName}» очищена."
            : $"Заметка события «{group.DisplayName}» сохранена.";
        await LoadEventsAsync(group.Id);
    }

    private async Task SplitSelectedEventAsync()
    {
        var group = SelectedEventGroup;
        var photo = SelectedEventPhoto;
        if (group is null || group.Id <= 0 || photo is null || group.PhotoCount < 2) return;
        var dialog = new EventNameWindow(group.DisplayName + " — часть 2") { Owner = Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.EventName.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = group.DisplayName + " — часть 2";
        var answer = MessageBox.Show(
            $"Разделить «{group.DisplayName}» с кадра:\n{photo.FileName}\n{photo.CaptureDateDisplay}\n\nВыбранный кадр и все более поздние кадры перейдут в новое событие «{name}». Меняется только SQLite-каталог; фотографии на диске не трогаются.",
            "Разделить событие", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var newId = await _database.SplitEventAsync(group.Id, photo.Id, name);
        StatusText = $"Событие разделено. Создано «{name}». Файлы на диске не изменялись.";
        await LoadEventsAsync(newId);
    }

    private async Task MergeSelectedEventAsync()
    {
        var source = SelectedEventGroup;
        if (source is null || source.Id <= 0) return;
        var targets = EventGroups.Where(x => x.Id != source.Id).ToList();
        if (targets.Count == 0) return;

        var dialog = new EventPickerWindow(targets)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true || dialog.SelectedEvent is null) return;
        var target = dialog.SelectedEvent;

        var answer = MessageBox.Show(
            $"Объединить события:\n\n«{source.DisplayName}» ({source.PhotoCount:N0} фото)\n+\n«{target.DisplayName}» ({target.PhotoCount:N0} фото)\n\n" +
            $"Останется событие «{target.DisplayName}»: его имя и обложка имеют приоритет.\n\n" +
            "Это меняет только локальный каталог событий. Исходные фотографии не перемещаются и не изменяются.",
            "Объединить события",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        await MergeEventIntoAsync(source.Id, target.Id);
    }

    public async Task MergeEventIntoAsync(long sourceEventId, long targetEventId)
    {
        if (!CanMoveEventPhotos) throw new InvalidOperationException("Дождитесь окончания текущего анализа или файловой операции.");
        if (sourceEventId <= 0 || targetEventId <= 0 || sourceEventId == targetEventId)
            throw new ArgumentException("Некорректные события для объединения.");

        var source = EventGroups.FirstOrDefault(x => x.Id == sourceEventId)
            ?? throw new InvalidOperationException("Исходное событие больше не существует.");
        var target = EventGroups.FirstOrDefault(x => x.Id == targetEventId)
            ?? throw new InvalidOperationException("Целевое событие больше не существует.");
        var targetName = target.DisplayName;

        IsFileOperationRunning = true;
        try
        {
            await _database.MergeEventsAsync(sourceEventId, targetEventId, targetName);
            await LoadEventsAsync(targetEventId);
            StatusText = $"Событие «{source.DisplayName}» объединено с «{targetName}». Оставлено имя целевого события. Файлы на диске не изменялись.";
            OrganizationPlan.Clear();
            OrganizationSummaryText = "События объединены — preview организации нужно построить заново.";
            RaiseOrganizationCommands();
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    public async Task MoveEventPhotosAsync(long sourceEventId, long targetEventId, IReadOnlyCollection<long> fileIds)
    {
        if (!CanMoveEventPhotos) throw new InvalidOperationException("Дождитесь окончания текущего анализа или файловой операции.");
        if (sourceEventId <= 0 || targetEventId <= 0 || sourceEventId == targetEventId)
            throw new ArgumentException("Некорректные события для переноса.");
        var ids = fileIds?.Where(x => x > 0).Distinct().ToArray() ?? [];
        if (ids.Length == 0) throw new ArgumentException("Не выбраны фотографии для переноса.");

        var sourceName = EventGroups.FirstOrDefault(x => x.Id == sourceEventId)?.DisplayName ?? "исходное событие";
        var targetName = EventGroups.FirstOrDefault(x => x.Id == targetEventId)?.DisplayName ?? "целевое событие";
        IsFileOperationRunning = true;
        try
        {
            var result = await _database.MoveEventPhotosAsync(sourceEventId, targetEventId, ids);
            if (result.Moved <= 0)
                throw new InvalidOperationException("Ни одна из выбранных фотографий больше не принадлежит исходному событию.");

            await LoadEventsAsync(targetEventId);
            StatusText = result.SourceDeleted
                ? $"Перенесено фото: {result.Moved:N0} · «{sourceName}» опустело и удалено только из каталога · цель: «{targetName}»."
                : $"Перенесено фото: {result.Moved:N0} · «{sourceName}» → «{targetName}». Файлы на диске не изменялись.";
            OrganizationPlan.Clear();
            OrganizationSummaryText = "Назначения событий изменились — preview организации нужно построить заново.";
            RaiseOrganizationCommands();
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    private async Task ShowSelectedEventPhotosAsync()
    {
        var selected = SelectedEventGroup;
        if (selected is null || selected.Id <= 0) return;
        SearchText = "";
        SelectedYear = "Все";
        SelectedCamera = "Все";
        SelectedReviewFilter = ReviewAll;
        SelectedSource = null;
        _personFilterId = null;
        _personFilterName = "";
        _eventFilterId = selected.Id;
        _eventFilterName = selected.DisplayName;
        SelectedMainTabIndex = 0;
        await LoadPhotosAsync(reset: true);
        StatusText = $"Библиотека отфильтрована по событию: {selected.DisplayName}. «Сбросить» уберёт этот фильтр.";
    }

    private void ShowEventPhotoInExplorer()
    {
        if (SelectedEventPhoto is null) return;
        ShowPathInExplorer(SelectedEventPhoto.FullPath);
    }

    private void OpenEventPhoto()
    {
        if (SelectedEventPhoto is null) return;
        OpenPath(SelectedEventPhoto.FullPath);
    }


    private void InvalidateOrganizationPlan(string message)
    {
        if (OrganizationPlan.Count > 0)
        {
            OrganizationPlan.Clear();
            SelectedOrganizationPlanItem = null;
        }
        OrganizationSummaryText = message;
        OrganizationProgressText = "";
        RaiseOrganizationCommands();
    }

    private async Task BuildOrganizationPlanAsync()
    {
        if (string.IsNullOrWhiteSpace(OrganizationDestinationRoot)) return;
        IsFileOperationRunning = true;
        try
        {
            StatusText = "Построение предварительного плана организации…";
            OrganizationProgressText = "Анализ путей без изменения файлов…";
            var plan = await _organizationService.BuildPlanAsync(
                OrganizationDestinationRoot,
                SelectedOrganizationLayoutMode,
                OrganizationUseFallbackDates,
                OrganizationIncludeDateInFileName,
                OrganizationIncludeNamedPeopleInFileName);
            OrganizationPlan.Clear();
            foreach (var item in plan)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(OrganizationPlanItem.IsSelected))
                    {
                        UpdateOrganizationSelectionSummary();
                        RaiseOrganizationCommands();
                    }
                };
                OrganizationPlan.Add(item);
            }
            SelectedOrganizationPlanItem = OrganizationPlan.FirstOrDefault();

            var ready = plan.Count(x => x.IsReady);
            var selected = plan.Count(x => x.IsSelected);
            var review = plan.Count(x => x.IsReady && !x.HasTrustedDate);
            var already = plan.Count(x => x.IsAlreadyCorrect);
            var renamed = plan.Count(x => x.WasAutoRenamed);
            var bytes = plan.Where(x => x.IsSelected).Sum(x => x.FileSize);
            OrganizationSummaryText = $"{SelectedOrganizationLayout}. Preview: всего {plan.Count:N0} · можно выполнить {ready:N0} · выбрано {selected:N0} ({ByteFormatter.Format(bytes)}) · без достоверной даты {review:N0} · уже на месте {already:N0} · безопасно переименовано из-за конфликтов {renamed:N0}.";
            OrganizationProgressText = "Это только preview. Файлы не перемещались.";
            StatusText = "План организации построен. Проверьте БЫЛО → СТАНЕТ и только потом запускайте выполнение.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Organization plan build failed", ex);
            OrganizationSummaryText = "Не удалось построить план: " + ex.Message;
            MessageBox.Show(ex.Message, "План организации", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
            RaiseOrganizationCommands();
        }
    }

    private void SelectTrustedOrganizationRows()
    {
        foreach (var item in OrganizationPlan) item.IsSelected = item.IsReady && item.HasTrustedDate;
        UpdateOrganizationSelectionSummary();
        RaiseOrganizationCommands();
    }

    private void ClearOrganizationSelection()
    {
        foreach (var item in OrganizationPlan) item.IsSelected = false;
        UpdateOrganizationSelectionSummary();
        RaiseOrganizationCommands();
    }

    private void UpdateOrganizationSelectionSummary()
    {
        var selected = OrganizationPlan.Where(x => x.IsSelected && x.IsReady).ToList();
        OrganizationProgressText = $"Выбрано к перемещению: {selected.Count:N0} · {ByteFormatter.Format(selected.Sum(x => x.FileSize))}.";
    }

    private async Task ExecuteOrganizationPlanAsync()
    {
        var selected = OrganizationPlan.Where(x => x.IsSelected && x.IsReady).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("В preview не выбрано ни одного файла.", "Организация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bytes = selected.Sum(x => x.FileSize);
        var untrusted = selected.Count(x => !x.HasTrustedDate);
        var executedLayout = SelectedOrganizationLayout;
        var executedWasQuickLayout = !OrganizationIsFullLayout;
        var executedDestinationRoot = selected[0].DestinationRoot;
        OrganizationBatchResult? completedResult = null;

        var first = MessageBox.Show(
            $"PAM физически переместит {selected.Count:N0} файлов ({ByteFormatter.Format(bytes)}) по показанным в preview путям.\n\n" +
            (untrusted > 0 ? $"ВНИМАНИЕ: вручную выбрано {untrusted:N0} файлов без достоверной даты.\n\n" : "") +
            $"Режим: {executedLayout}.\n\n" +
            "Каждый файл проверяется SHA-256. Ничего не перезаписывается. Операции записываются в журнал и имеют Undo.\n\nПродолжить?",
            "Выполнить план организации", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (first != MessageBoxResult.Yes) return;

        var second = MessageBox.Show(
            "Последнее подтверждение. Сейчас начнётся ФИЗИЧЕСКОЕ перемещение файлов.\n\n" +
            "При остановке уже успешно перемещённые файлы останутся на новых местах и будут доступны в журнале Undo. Начать?",
            "Подтвердить перемещение", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (second != MessageBoxResult.Yes) return;

        IsFileOperationRunning = true;
        IsOrganizationMoveRunning = true;
        var last = new OrganizationProgress("", selected.Count, 0, 0, 0, 0, "");
        var progress = new Progress<OrganizationProgress>(p =>
        {
            last = p;
            OrganizationProgressText = $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} · успешно {p.SucceededFiles:N0} · ошибок {p.FailedFiles:N0} · перемещено {ByteFormatter.Format(p.BytesMoved)}";
            StatusText = p.Stage + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : ": " + TruncateMiddle(p.CurrentFile, 90));
            RaiseOrganizationCommands();
        });

        try
        {
            completedResult = await _organizationService.ExecuteAsync(selected, progress);
            StatusText = completedResult.Failed == 0
                ? $"Организация завершена: перемещено {completedResult.Succeeded:N0} файлов ({ByteFormatter.Format(completedResult.BytesMoved)})."
                : $"Организация завершена: успешно {completedResult.Succeeded:N0}, ошибок {completedResult.Failed:N0}.";
            if (completedResult.Errors.Count > 0)
            {
                var details = string.Join("\n", completedResult.Errors.Take(10));
                if (completedResult.Errors.Count > 10) details += $"\n…ещё {completedResult.Errors.Count - 10:N0}. Подробности в логе.";
                MessageBox.Show(details, "Часть файлов не перемещена", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Организация остановлена. Уже выполнено: {last.SucceededFiles:N0}; ошибки: {last.FailedFiles:N0}. Выполненные операции сохранены в Undo.";
            OrganizationProgressText += " · остановлено пользователем";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Organization execution failed", ex);
            StatusText = "Ошибка организации: " + ex.Message;
            MessageBox.Show(ex.Message, "Организация", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsOrganizationMoveRunning = false;
            IsFileOperationRunning = false;
            OrganizationPlan.Clear();
            SelectedOrganizationPlanItem = null;
            OrganizationSummaryText = "Файловая структура изменилась. Постройте новый preview перед следующей операцией.";
            await LoadSourcesAsync();
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            await LoadOrganizationHistoryAsync();
            RaiseOrganizationCommands();
        }

        if (completedResult is { Succeeded: > 0 } && executedWasQuickLayout)
        {
            var followUp = MessageBox.Show(
                $"Быстрая раскладка «{executedLayout}» закончена.\n\n" +
                $"Новый корень:\n{executedDestinationRoot}\n\n" +
                "Взять получившуюся структуру за основу дальнейшей сортировки?\n\n" +
                "ДА — переключить режим на полную организацию и сразу построить новый Preview в этом же корне.\n" +
                "НЕТ — оставить быструю раскладку как итоговую структуру.\n\n" +
                "Дополнительные копии фотографий PAM при этом не создаёт: это тот же безопасный перенос с SHA-256 и Undo.",
                "Продолжить сортировку?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (followUp == MessageBoxResult.Yes)
            {
                SelectedOrganizationLayout = OrganizationLayoutFull;
                OrganizationDestinationRoot = executedDestinationRoot;
                await BuildOrganizationPlanAsync();
                StatusText = "Быстрая раскладка принята за основу. Построен Preview полной организации; файлы пока больше не перемещались.";
            }
            else
            {
                OrganizationSummaryText = $"Быстрая раскладка «{executedLayout}» оставлена как итоговая структура. Для следующего этапа можно выбрать режим «{OrganizationLayoutFull}» и построить Preview в том же корне.";
            }
        }
    }


    private async Task LoadOrganizationHistoryAsync()
    {
        var selectedId = SelectedOrganizationMove?.Id;
        var items = await _database.GetOrganizationMovesAsync();
        OrganizationMoves.Clear();
        foreach (var item in items) OrganizationMoves.Add(item);
        SelectedOrganizationMove = OrganizationMoves.FirstOrDefault(x => x.Id == selectedId) ?? OrganizationMoves.FirstOrDefault();
    }

    private async Task UndoSelectedOrganizationMoveAsync()
    {
        var action = SelectedOrganizationMove;
        if (action?.IsActive != true) return;
        var answer = MessageBox.Show(
            $"Вернуть файл на исходное место?\n\n{action.NewPath}\n→\n{action.OriginalPath}\n\n" +
            "PAM проверит SHA-256 и не перезапишет существующий файл.",
            "Undo организации", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsFileOperationRunning = true;
        try
        {
            await _organizationService.UndoAsync(action);
            StatusText = "Перемещение отменено: файл возвращён на исходное место.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Organization undo failed", ex);
            MessageBox.Show(ex.Message, "Undo организации", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = "Не удалось отменить перемещение: " + ex.Message;
        }
        finally
        {
            IsFileOperationRunning = false;
            await LoadSourcesAsync();
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            await LoadOrganizationHistoryAsync();
            RaiseOrganizationCommands();
        }
    }

    private async Task RefreshAllAsync()
    {
        // Sources and filter values define the photo query. The latest reversible face-ignore
        // action is independent, so load it alongside the other read-only startup state.
        var latestFaceIgnoreTask = _database.GetLatestActiveFaceIgnoreActionAsync();
        await Task.WhenAll(LoadSourcesAsync(), LoadFilterValuesAsync());
        LatestFaceIgnoreAction = await latestFaceIgnoreTask;

        await Task.WhenAll(
            LoadPhotosAsync(reset: true),
            LoadStatisticsAsync(),
            LoadDuplicateGroupsAsync(),
            LoadPeopleAsync(),
            LoadEventsAsync(),
            LoadTimelineAsync(),
            LoadOrganizationHistoryAsync(),
            LoadQuarantineAsync());
        RaiseScanCommands();
        RaiseDuplicateCommands();
        RaiseVisualCommands();
        RaiseBurstCommands();
        RaisePeopleCommands();
        RaiseEventCommands();
        RaiseOrganizationCommands();
        RaiseQuarantineCommands();
    }

    private async Task LoadSourcesAsync()
    {
        var selectedPath = SelectedSource?.Path;
        var sources = await _database.GetSourceFoldersAsync();
        Sources.Clear();
        foreach (var source in sources) Sources.Add(source);
        SelectedSource = sources.FirstOrDefault(x => string.Equals(x.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadFilterValuesAsync()
    {
        var oldYear = SelectedYear;
        var oldCamera = SelectedCamera;
        var yearsTask = _database.GetYearsAsync();
        var camerasTask = _database.GetCamerasAsync();
        await Task.WhenAll(yearsTask, camerasTask);

        Years.Clear();
        Years.Add("Все");
        foreach (var year in await yearsTask) Years.Add(year.ToString());
        SelectedYear = Years.Contains(oldYear) ? oldYear : "Все";

        Cameras.Clear();
        Cameras.Add("Все");
        foreach (var camera in await camerasTask) Cameras.Add(camera);
        SelectedCamera = Cameras.Contains(oldCamera) ? oldCamera : "Все";
    }

    private async Task LoadTimelineAsync(bool preserveSelection = true)
    {
        var oldKey = preserveSelection && SelectedTimelineMonth is not null
            ? (SelectedTimelineMonth.Year, SelectedTimelineMonth.Month)
            : ((int Year, int Month)?)null;
        var months = await _database.GetTimelineMonthsAsync();
        TimelineYears.Clear();
        foreach (var yearGroup in months.GroupBy(x => x.Year).OrderByDescending(x => x.Key))
        {
            TimelineYears.Add(new TimelineYearItem
            {
                Year = yearGroup.Key,
                PhotoCount = yearGroup.Sum(x => x.PhotoCount),
                Months = yearGroup.OrderByDescending(x => x.Month).ToList()
            });
        }
        var all = TimelineYears.SelectMany(x => x.Months).ToList();
        _suppressTimelinePhotoLoad = true;
        try
        {
            SelectedTimelineMonth = oldKey.HasValue
                ? all.FirstOrDefault(x => x.Year == oldKey.Value.Year && x.Month == oldKey.Value.Month) ?? all.FirstOrDefault()
                : all.FirstOrDefault();
        }
        finally
        {
            _suppressTimelinePhotoLoad = false;
        }

        if (SelectedMainTabIndex == 6 && SelectedTimelineMonth is not null)
            await LoadSelectedTimelineMonthAsync();
        else
        {
            TimelinePhotos.Clear();
            SelectedTimelinePhoto = null;
            OnPropertyChanged(nameof(TimelineSummaryText));
        }
    }

    private async Task LoadSelectedTimelineMonthAsync()
    {
        var month = SelectedTimelineMonth;
        TimelinePhotos.Clear();
        SelectedTimelinePhoto = null;
        OnPropertyChanged(nameof(TimelineSummaryText));
        if (month is null) return;
        try
        {
            var items = await _database.QueryPhotosAsync(new PhotoQuery
            {
                DateFrom = month.StartDate,
                DateToExclusive = month.EndDateExclusive,
                Limit = 10000,
                Offset = 0
            });
            if (SelectedTimelineMonth?.Year != month.Year || SelectedTimelineMonth?.Month != month.Month) return;
            foreach (var item in items.OrderBy(x => x.CaptureDate)) TimelinePhotos.Add(item);
            SelectedTimelinePhoto = TimelinePhotos.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Loading timeline month failed", ex);
            StatusText = "Не удалось загрузить хронологию: " + ex.Message;
        }
    }

    private bool CanEditSelectedPhotoDate() =>
        SelectedPhoto is not null &&
        !IsScanning && !IsDuplicateAnalysisRunning && !IsVisualAnalysisRunning && !IsQualityAnalysisRunning &&
        !IsBurstAnalysisRunning && !IsPeopleAnalysisRunning && !IsEventAnalysisRunning &&
        !IsFileOperationRunning;

    private async Task EditSelectedPhotoCaptureDateAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null) return;

        var initialDate = StoredDateTime.TryParse(photo.CaptureDate, out var parsed) ? parsed : DateTime.Today;
        var dialog = new CaptureDateEditorWindow(initialDate, photo.CaptureDateSource)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        IsFileOperationRunning = true;
        try
        {
            await _database.SetManualCaptureDateAsync(photo.Id, dialog.CaptureDate);
            StatusText = $"Дата «{photo.FileName}» исправлена только в каталоге PAM. Оригинальный файл не изменён.";
            await RefreshAfterCaptureDateChangeAsync(photo.Id);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Manual capture date update failed", ex);
            StatusText = "Не удалось изменить дату в каталоге: " + ex.Message;
            MessageBox.Show(ex.Message, "Дата в каталоге", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    private async Task ResetSelectedPhotoCaptureDateAsync()
    {
        var photo = SelectedPhoto;
        if (photo is null || !photo.IsManualCaptureDate) return;

        var answer = MessageBox.Show(
            $"Вернуть для «{photo.FileName}» дату, автоматически найденную сканером?\n\nРучная дата в каталоге будет забыта. Оригинальный файл по-прежнему не изменяется.",
            "Вернуть автоматическую дату",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsFileOperationRunning = true;
        try
        {
            if (await _database.ResetManualCaptureDateAsync(photo.Id))
            {
                StatusText = $"Для «{photo.FileName}» восстановлена автоматически найденная дата. Оригинальный файл не изменён.";
                await RefreshAfterCaptureDateChangeAsync(photo.Id);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Manual capture date reset failed", ex);
            StatusText = "Не удалось вернуть автоматическую дату: " + ex.Message;
            MessageBox.Show(ex.Message, "Дата в каталоге", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
        }
    }

    private async Task RefreshAfterCaptureDateChangeAsync(long photoId)
    {
        await LoadFilterValuesAsync();
        await LoadPhotosAsync(reset: true, preferredPhotoId: photoId);
        await LoadEventsAsync();
        OrganizationPlan.Clear();
        OrganizationSummaryText = "Дата в каталоге изменилась — preview организации нужно построить заново.";
        RaiseOrganizationCommands();
    }

    private async Task LoadPhotosAsync(bool reset, long? preferredPhotoId = null)
    {
        var selectedId = preferredPhotoId ?? SelectedPhoto?.Id;
        var offset = reset ? 0 : Photos.Count;
        var query = BuildQuery(offset);
        List<PhotoItem> items;
        if (reset)
        {
            // Count and first page are independent read queries. Running them together avoids
            // paying two full SQLite round-trips before the library becomes visible.
            var countTask = _database.CountPhotosAsync(query);
            var itemsTask = _database.QueryPhotosAsync(query);
            await Task.WhenAll(countTask, itemsTask);
            _filteredTotal = await countTask;
            items = await itemsTask;
            Photos.Clear();
        }
        else
        {
            items = await _database.QueryPhotosAsync(query);
        }

        foreach (var item in items) Photos.Add(item);
        if (reset)
            SelectedPhoto = selectedId.HasValue ? Photos.FirstOrDefault(x => x.Id == selectedId.Value) ?? Photos.FirstOrDefault() : Photos.FirstOrDefault();
        else if (SelectedPhoto is null && Photos.Count > 0)
            SelectedPhoto = Photos[0];
        LoadedText = $"Показано {Photos.Count:N0} из {_filteredTotal:N0}";
        var baseHeader = SelectedSource is null ? "Фотографии" : "Фотографии — " + SelectedSource.DisplayName;
        if (_eventFilterId.HasValue)
            baseHeader += " · событие: " + _eventFilterName;
        if (_personFilterId.HasValue)
            baseHeader += " · человек: " + _personFilterName;
        ListHeaderText = baseHeader;
        LoadMoreCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadDuplicateGroupsAsync()
    {
        var selectedHash = SelectedDuplicateGroup?.Sha256;
        var groups = await _database.GetExactDuplicateGroupsAsync();

        DuplicateGroups.Clear();
        foreach (var group in groups) DuplicateGroups.Add(group);
        RebuildDuplicateFolderGroups();

        SelectedDuplicateGroup = DuplicateGroups.FirstOrDefault(x => x.Sha256 == selectedHash) ?? DuplicateGroups.FirstOrDefault();

        // The loaded groups already contain every value needed for the summary. Avoid a second
        // full GROUP BY over Files on every refresh, which is noticeable on large catalogs.
        var duplicateFiles = groups.Sum(x => (long)x.FileCount);
        var extraCopies = groups.Sum(x => Math.Max(0L, x.FileCount - 1L));
        var wastedBytes = groups.Sum(x => x.WastedBytes);
        DuplicateSummaryText = groups.Count == 0
            ? "Точных дублей пока не найдено. Нажмите «Найти точные дубли», чтобы рассчитать SHA-256 для файлов-кандидатов."
            : $"Групп: {groups.Count:N0} · файлов в группах: {duplicateFiles:N0} · " +
              $"лишних точных копий: {extraCopies:N0} · потенциально освободится: {ByteFormatter.Format(wastedBytes)}";
    }

    private void RebuildDuplicateFolderGroups()
    {
        var folders = new Dictionary<string, DuplicateFolderAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in DuplicateGroups)
        {
            var byFolder = group.Files
                .Select(file => (File: file, Folder: GetContainingFolder(file.FullPath)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Folder))
                .GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Select(y => y.File).ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var pair in byFolder)
            {
                if (!folders.TryGetValue(pair.Key, out var accumulator))
                {
                    accumulator = new DuplicateFolderAccumulator(pair.Key);
                    folders[pair.Key] = accumulator;
                }

                var inside = pair.Value;
                var outsideCount = Math.Max(0, group.FileCount - inside.Count);
                accumulator.ExactSetCount++;
                accumulator.DuplicateFileCount += inside.Count;
                accumulator.InternalExtraCount += Math.Max(0, inside.Count - 1);
                if (outsideCount == 0) accumulator.RequiredKeeperCount++;

                var removableHere = outsideCount > 0 ? inside.Count : Math.Max(0, inside.Count - 1);
                accumulator.RemovableFromFolderCount += removableHere;
                accumulator.RemovableFromFolderBytes += group.FileSize * removableHere;
                accumulator.RemovableElsewhereCount += outsideCount;
                accumulator.RemovableElsewhereBytes += group.FileSize * outsideCount;

                foreach (var other in byFolder)
                {
                    if (string.Equals(other.Key, pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!accumulator.Matches.TryGetValue(other.Key, out var match))
                    {
                        match = new DuplicateFolderMatchAccumulator(other.Key);
                        accumulator.Matches[other.Key] = match;
                    }
                    match.SharedSetCount++;
                    match.SelectedCopyCount += inside.Count;
                    match.SelectedBytes += group.FileSize * inside.Count;
                    match.OtherCopyCount += other.Value.Count;
                    match.OtherBytes += group.FileSize * other.Value.Count;
                }
            }
        }

        var items = folders.Values
            .Select(x => new DuplicateFolderItem
            {
                FolderPath = x.FolderPath,
                ExactSetCount = x.ExactSetCount,
                DuplicateFileCount = x.DuplicateFileCount,
                RemovableFromFolderCount = x.RemovableFromFolderCount,
                RemovableFromFolderBytes = x.RemovableFromFolderBytes,
                RemovableElsewhereCount = x.RemovableElsewhereCount,
                RemovableElsewhereBytes = x.RemovableElsewhereBytes,
                OtherFolderCount = x.Matches.Count,
                InternalExtraCount = x.InternalExtraCount,
                RequiredKeeperCount = x.RequiredKeeperCount,
                Matches = x.Matches.Values
                    .OrderByDescending(m => m.SelectedBytes + m.OtherBytes)
                    .ThenBy(m => m.OtherFolderPath, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new DuplicateFolderMatchItem
                    {
                        OtherFolderPath = m.OtherFolderPath,
                        SharedSetCount = m.SharedSetCount,
                        SelectedCopyCount = m.SelectedCopyCount,
                        SelectedBytes = m.SelectedBytes,
                        OtherCopyCount = m.OtherCopyCount,
                        OtherBytes = m.OtherBytes
                    })
                    .ToList()
            })
            .OrderByDescending(x => x.RemovableFromFolderBytes)
            .ThenByDescending(x => x.RemovableFromFolderCount)
            .ThenBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DuplicateFolderGroups.Clear();
        foreach (var item in items) DuplicateFolderGroups.Add(item);
        RebuildDuplicateFolderIntersections();
    }

    private void RebuildDuplicateFolderIntersections()
    {
        var selectedKey = SelectedDuplicateFolderIntersection?.PairKey;
        var lookup = DuplicateFolderGroups.ToDictionary(x => x.FolderPath, StringComparer.OrdinalIgnoreCase);
        var raw = new List<DuplicateFolderIntersectionItem>();

        foreach (var left in DuplicateFolderGroups)
        {
            foreach (var match in left.Matches)
            {
                if (StringComparer.OrdinalIgnoreCase.Compare(left.FolderPath, match.OtherFolderPath) >= 0) continue;
                if (!lookup.TryGetValue(match.OtherFolderPath, out var right)) continue;

                raw.Add(new DuplicateFolderIntersectionItem
                {
                    FolderAPath = left.FolderPath,
                    FolderBPath = right.FolderPath,
                    SharedSetCount = match.SharedSetCount,
                    ACopyCount = match.SelectedCopyCount,
                    ABytes = match.SelectedBytes,
                    BCopyCount = match.OtherCopyCount,
                    BBytes = match.OtherBytes,
                    ATotalDuplicateSetCount = left.ExactSetCount,
                    BTotalDuplicateSetCount = right.ExactSetCount
                });
            }
        }

        var sorted = raw
            .Where(x => !ShowOnlyCompleteDuplicateFolderPairs || x.CanFullyClearEitherSide)
            .OrderByDescending(x => x.MaxRemovableBytes)
            .ThenByDescending(x => x.SharedSetCount)
            .ThenBy(x => x.FolderAPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FolderBPath, StringComparer.OrdinalIgnoreCase)
            .Select((x, index) => new DuplicateFolderIntersectionItem
            {
                DisplayNumber = index + 1,
                FolderAPath = x.FolderAPath,
                FolderBPath = x.FolderBPath,
                SharedSetCount = x.SharedSetCount,
                ACopyCount = x.ACopyCount,
                ABytes = x.ABytes,
                BCopyCount = x.BCopyCount,
                BBytes = x.BBytes,
                ATotalDuplicateSetCount = x.ATotalDuplicateSetCount,
                BTotalDuplicateSetCount = x.BTotalDuplicateSetCount
            })
            .ToList();

        DuplicateFolderIntersections.Clear();
        foreach (var pair in sorted) DuplicateFolderIntersections.Add(pair);

        SelectedDuplicateFolderIntersection = DuplicateFolderIntersections.FirstOrDefault(x =>
                                                string.Equals(x.PairKey, selectedKey, StringComparison.OrdinalIgnoreCase))
                                            ?? DuplicateFolderIntersections.FirstOrDefault();
    }

    private void RebuildDuplicateFolderPairRows()
    {
        DuplicateFolderPairRows.Clear();

        var leftFolder = SelectedDuplicateFolderIntersection?.FolderAPath;
        var rightFolder = SelectedDuplicateFolderIntersection?.FolderBPath;
        if (string.IsNullOrWhiteSpace(leftFolder) || string.IsNullOrWhiteSpace(rightFolder))
        {
            OnPropertyChanged(nameof(DuplicatePairSummaryText));
            OnPropertyChanged(nameof(DuplicatePairSelectionText));
            return;
        }

        var leftNormalized = NormalizeFolderPath(leftFolder);
        var rightNormalized = NormalizeFolderPath(rightFolder);
        var rows = new List<DuplicateFolderPairItem>();

        foreach (var group in DuplicateGroups)
        {
            var leftFiles = group.Files
                .Where(x => string.Equals(GetContainingFolder(x.FullPath), leftNormalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (leftFiles.Count == 0) continue;

            var rightFiles = group.Files
                .Where(x => string.Equals(GetContainingFolder(x.FullPath), rightNormalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (rightFiles.Count == 0) continue;

            rows.Add(new DuplicateFolderPairItem
            {
                Sha256 = group.Sha256,
                FileSize = group.FileSize,
                LeftCopyCount = leftFiles.Count,
                RightCopyCount = rightFiles.Count,
                LeftFilesText = FormatDuplicatePairFiles(leftFiles),
                RightFilesText = FormatDuplicatePairFiles(rightFiles)
            });
        }

        foreach (var row in rows
                     .OrderByDescending(x => x.FileSize * (x.LeftCopyCount + x.RightCopyCount))
                     .ThenBy(x => x.LeftFilesText, StringComparer.OrdinalIgnoreCase))
            DuplicateFolderPairRows.Add(row);

        OnPropertyChanged(nameof(DuplicatePairSummaryText));
        OnPropertyChanged(nameof(DuplicatePairSelectionText));
        QuarantineDuplicateFolderPairSideCommand?.RaiseCanExecuteChanged();
    }

    private static string FormatDuplicatePairFiles(IReadOnlyList<DuplicateFileItem> files)
    {
        const int maxShown = 4;
        var names = files.Take(maxShown).Select(x => x.FileName).ToList();
        if (files.Count > maxShown) names.Add($"+ ещё {files.Count - maxShown:N0}");
        return string.Join(Environment.NewLine, names);
    }

    private List<(DuplicateGroupItem Group, DuplicateFileItem Keeper, IReadOnlyList<DuplicateFileItem> Candidates)> BuildDuplicateFolderPairCleanupPlan(
        string leftFolderPath,
        string rightFolderPath,
        bool removeLeft)
    {
        var leftNormalized = NormalizeFolderPath(leftFolderPath);
        var rightNormalized = NormalizeFolderPath(rightFolderPath);
        var result = new List<(DuplicateGroupItem Group, DuplicateFileItem Keeper, IReadOnlyList<DuplicateFileItem> Candidates)>();

        foreach (var group in DuplicateGroups)
        {
            var leftFiles = group.Files
                .Where(x => string.Equals(GetContainingFolder(x.FullPath), leftNormalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (leftFiles.Count == 0) continue;

            var rightFiles = group.Files
                .Where(x => string.Equals(GetContainingFolder(x.FullPath), rightNormalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (rightFiles.Count == 0) continue;

            var keeper = removeLeft ? rightFiles[0] : leftFiles[0];
            IReadOnlyList<DuplicateFileItem> candidates = removeLeft ? leftFiles : rightFiles;
            result.Add((group, keeper, candidates));
        }

        return result;
    }

    private async Task QuarantineSelectedDuplicateFolderPairSideAsync()
    {
        var pair = SelectedDuplicateFolderIntersection;
        if (pair is null) return;

        var removeLeft = RemoveDuplicatePairFromLeft;
        var deleteDirectly = DeleteExactDuplicatesDirectly;
        var plan = BuildDuplicateFolderPairCleanupPlan(pair.FolderAPath, pair.FolderBPath, removeLeft);
        var count = plan.Sum(x => x.Candidates.Count);
        var bytes = plan.Sum(x => x.Candidates.Sum(file => file.FileSize));
        if (count == 0)
        {
            MessageBox.Show(
                deleteDirectly ? "У выбранной пары папок больше нет точных совпадений для удаления." : "У выбранной пары папок больше нет точных совпадений для карантина.",
                "Сравнение папок A ↔ B", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var source = removeLeft ? pair.FolderAPath : pair.FolderBPath;
        var keeperFolder = removeLeft ? pair.FolderBPath : pair.FolderAPath;
        var side = removeLeft ? "A" : "B";
        var actionText = deleteDirectly ? "БЕЗВОЗВРАТНО УДАЛИТЬ" : "Отправить в карантин";
        var safetyText = deleteDirectly
            ? "КАРАНТИН И UNDO НЕ ИСПОЛЬЗУЮТСЯ. Перед каждым удалением PAM заново проверяет полный SHA-256 и удерживает проверенную сохраняемую копию открытой, чтобы она не могла исчезнуть во время операции."
            : "Перед каждым переносом PAM повторно проверяет сохраняемую и удаляемую копии; операция обратима через Undo.";

        var answer = MessageBox.Show(
            $"Пара папок A ↔ B подтверждена полным SHA-256.\n\n" +
            $"{actionText} ВСЕ совпадающие файлы со стороны {side}:\n{source}\n\n" +
            $"Файлов: {count:N0} · {ByteFormatter.Format(bytes)}\n\n" +
            $"Сохраняемая сторона:\n{keeperFolder}\n\n" +
            "Затрагиваются только SHA-256, присутствующие одновременно в обеих выбранных папках. Другие файлы и другие папки не трогаются.\n\n" +
            safetyText,
            deleteDirectly ? "БЕЗВОЗВРАТНОЕ удаление стороны A ↔ B" : "Карантин выбранной стороны A ↔ B",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        await ExecuteDuplicateFolderCleanupPlanAsync(
            plan,
            deleteDirectly ? $"Удаление совпадений из стороны {side}…" : $"Карантин совпадений из стороны {side}…",
            deleteDirectly);
    }

    private async Task ExecuteDuplicateFolderCleanupPlanAsync(
        IReadOnlyList<(DuplicateGroupItem Group, DuplicateFileItem Keeper, IReadOnlyList<DuplicateFileItem> Candidates)> plan,
        string progressText,
        bool deleteDirectly)
    {
        var requested = plan.Sum(x => x.Candidates.Count);
        var succeeded = 0;
        long bytesProcessed = 0;
        var errors = new List<string>();

        IsFileOperationRunning = true;
        try
        {
            StatusText = progressText;
            for (var index = 0; index < plan.Count; index++)
            {
                var item = plan[index];
                StatusText = $"{progressText} Набор {index + 1:N0} из {plan.Count:N0}…";
                try
                {
                    if (deleteDirectly)
                    {
                        var result = await _quarantineService.DeleteExactCandidatesPermanentlyAsync(item.Group, item.Keeper, item.Candidates);
                        succeeded += result.Succeeded;
                        bytesProcessed += result.BytesDeleted;
                        errors.AddRange(result.Errors);
                    }
                    else
                    {
                        var result = await _quarantineService.QuarantineExactCandidatesAsync(item.Group, item.Keeper, item.Candidates);
                        succeeded += result.Succeeded;
                        bytesProcessed += result.BytesMoved;
                        errors.AddRange(result.Errors);
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Error(deleteDirectly
                        ? $"Folder duplicate group direct delete failed: {item.Group.HashShort}"
                        : $"Folder duplicate group quarantine failed: {item.Group.HashShort}", ex);
                    errors.Add($"Набор {item.Group.HashShort}: {ex.Message}");
                }
            }

            if (errors.Count == 0)
            {
                StatusText = deleteDirectly
                    ? $"Безвозвратно удалено {succeeded:N0} файлов ({ByteFormatter.Format(bytesProcessed)})."
                    : $"В карантин перемещено {succeeded:N0} файлов ({ByteFormatter.Format(bytesProcessed)}).";
            }
            else
            {
                StatusText = deleteDirectly
                    ? $"Удаление: запрошено {requested:N0}, успешно {succeeded:N0}, ошибок {errors.Count:N0}."
                    : $"Карантин: запрошено {requested:N0}, успешно {succeeded:N0}, ошибок {errors.Count:N0}.";
                var details = string.Join("\n", errors.Take(8));
                if (errors.Count > 8) details += $"\n…и ещё {errors.Count - 8:N0}. Подробности есть в журнале.";
                MessageBox.Show(details, deleteDirectly ? "Не все файлы удалось удалить" : "Не все файлы удалось переместить", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(deleteDirectly ? "Folder duplicate direct delete failed" : "Folder duplicate quarantine failed", ex);
            StatusText = (deleteDirectly ? "Ошибка удаления: " : "Ошибка карантина: ") + ex.Message;
            MessageBox.Show(ex.Message, deleteDirectly ? "Ошибка безвозвратного удаления" : "Ошибка карантина", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
            ClearVisualGroupsAfterLibraryChange();
            await LoadDuplicateGroupsAsync();
            await LoadQuarantineAsync();
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            await LoadEventsAsync();
        }
    }

    private void ShowSelectedDuplicateFolderInExplorer()
    {
        var folder = SelectedDuplicateFolderIntersection?.FolderAPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void ShowSelectedDuplicateFolderMatchInExplorer()
    {
        var folder = SelectedDuplicateFolderIntersection?.FolderBPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private static string GetContainingFolder(string fullPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(fullPath));
            return string.IsNullOrWhiteSpace(directory) ? "" : NormalizeFolderPath(directory);
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeFolderPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return root;
        return trimmed;
    }

    private async Task QuarantineOtherCopiesAsync()
    {
        var group = SelectedDuplicateGroup;
        var keeper = SelectedDuplicateFile;
        if (group is null || keeper is null || group.Files.Count < 2) return;

        var deleteDirectly = DeleteExactDuplicatesDirectly;
        var count = group.Files.Count - 1;
        var bytes = group.FileSize * count;
        var answer = MessageBox.Show(
            $"Оставить этот файл:\n{keeper.FullPath}\n\n" +
            (deleteDirectly
                ? $"А остальные точные копии ({count:N0}, {ByteFormatter.Format(bytes)}) БЕЗВОЗВРАТНО УДАЛИТЬ?\n\n"
                : $"А остальные точные копии ({count:N0}, {ByteFormatter.Format(bytes)}) переместить в безопасный карантин?\n\n") +
            (deleteDirectly
                ? "КАРАНТИН И UNDO НЕ ИСПОЛЬЗУЮТСЯ. PAM повторно проверит полный SHA-256 сохраняемой копии, удержит её от изменения на время операции и заново проверит каждый удаляемый файл непосредственно перед удалением."
                : "Перед каждой операцией SHA-256 будет пересчитан. Файлы можно вернуть через Undo."),
            deleteDirectly ? "БЕЗВОЗВРАТНО удалить точные копии" : "Подтвердить карантин точных дублей",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var currentIndex = DuplicateGroups.IndexOf(group);
        var nextGroupHash = currentIndex >= 0 && DuplicateGroups.Count > 1
            ? DuplicateGroups[(currentIndex + 1) % DuplicateGroups.Count].Sha256
            : null;
        var advanceAfterSuccess = false;

        IsFileOperationRunning = true;
        try
        {
            if (deleteDirectly)
            {
                StatusText = "Проверка SHA-256 и безвозвратное удаление точных копий…";
                var result = await _quarantineService.DeleteAllExactCopiesExceptAsync(group, keeper);
                advanceAfterSuccess = result.Failed == 0 && result.Succeeded > 0;
                if (result.Failed == 0)
                {
                    StatusText = $"Безвозвратно удалено {result.Succeeded:N0} файлов ({ByteFormatter.Format(result.BytesDeleted)}).";
                }
                else
                {
                    StatusText = $"Удаление: успешно {result.Succeeded:N0}, ошибок {result.Failed:N0}.";
                    var details = string.Join("\n", result.Errors.Take(8));
                    if (result.Errors.Count > 8) details += $"\n…и ещё {result.Errors.Count - 8:N0}. Подробности есть в журнале.";
                    MessageBox.Show(details, "Не все файлы удалось удалить", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                StatusText = "Проверка SHA-256 и перенос точных копий в карантин…";
                var result = await _quarantineService.QuarantineAllExceptAsync(group, keeper);
                advanceAfterSuccess = result.Failed == 0 && result.Succeeded > 0;
                if (result.Failed == 0)
                {
                    StatusText = $"В карантин перемещено {result.Succeeded:N0} файлов ({ByteFormatter.Format(result.BytesMoved)}).";
                }
                else
                {
                    StatusText = $"Карантин: успешно {result.Succeeded:N0}, ошибок {result.Failed:N0}.";
                    var details = string.Join("\n", result.Errors.Take(8));
                    if (result.Errors.Count > 8) details += $"\n…и ещё {result.Errors.Count - 8:N0}. Подробности есть в журнале.";
                    MessageBox.Show(details, "Не все файлы удалось переместить", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error(deleteDirectly ? "Direct exact duplicate delete batch failed" : "Quarantine batch failed", ex);
            StatusText = (deleteDirectly ? "Ошибка удаления: " : "Ошибка карантина: ") + ex.Message;
            MessageBox.Show(ex.Message, deleteDirectly ? "Ошибка безвозвратного удаления" : "Ошибка карантина", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
            ClearVisualGroupsAfterLibraryChange();
            await LoadDuplicateGroupsAsync();
            if (advanceAfterSuccess && nextGroupHash is not null)
                SelectedDuplicateGroup = DuplicateGroups.FirstOrDefault(x => string.Equals(x.Sha256, nextGroupHash, StringComparison.OrdinalIgnoreCase)) ?? SelectedDuplicateGroup;
            await LoadQuarantineAsync();
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            await LoadEventsAsync();
        }
    }

    private async Task UndoSelectedQuarantineAsync()
    {
        var action = SelectedQuarantineAction;
        if (action is null || !action.IsActive) return;

        var answer = MessageBox.Show(
            $"Вернуть файл из карантина на исходное место?\n\n{action.OriginalPath}\n\n" +
            "Если по исходному пути уже существует файл, PAM ничего не перезапишет и остановит Undo.",
            "Undo карантина",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsFileOperationRunning = true;
        try
        {
            StatusText = "Проверка SHA-256 и восстановление файла…";
            await _quarantineService.UndoAsync(action);
            StatusText = "Файл восстановлен из карантина: " + action.OriginalPath;
        }
        catch (Exception ex)
        {
            LoggingService.Error("Quarantine undo failed", ex);
            StatusText = "Undo не выполнен: " + ex.Message;
            MessageBox.Show(ex.Message, "Undo не выполнен", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
            ClearVisualGroupsAfterLibraryChange();
            await LoadQuarantineAsync();
            await LoadDuplicateGroupsAsync();
            await LoadPhotosAsync(reset: true);
            await LoadStatisticsAsync();
            await LoadEventsAsync();
        }
    }

    private async Task PermanentDeleteSelectedQuarantineAsync()
    {
        var action = SelectedQuarantineAction;
        if (!AllowPermanentDelete || action is null || !action.IsActive) return;

        var first = MessageBox.Show(
            $"ОКОНЧАТЕЛЬНО удалить файл из карантина?\n\n{action.QuarantinePath}\n\n" +
            $"Размер: {action.FileSizeDisplay}\n\nПосле этого Undo будет невозможен.",
            "Опасная операция — подтверждение 1 из 2",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (first != MessageBoxResult.Yes) return;

        var second = MessageBox.Show(
            "ПОСЛЕДНЕЕ ПРЕДУПРЕЖДЕНИЕ\n\n" +
            "Файл будет физически удалён с диска. Восстановить его средствами PAM будет невозможно.\n\n" +
            "Вы действительно хотите продолжить?",
            "Окончательное удаление — подтверждение 2 из 2",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error,
            MessageBoxResult.No);
        if (second != MessageBoxResult.Yes) return;

        IsFileOperationRunning = true;
        try
        {
            StatusText = "Повторная проверка SHA-256 перед окончательным удалением…";
            await _quarantineService.DeletePermanentlyAsync(action);
            StatusText = "Файл окончательно удалён из карантина. Операция записана в журнал.";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Permanent quarantine delete failed", ex);
            StatusText = "Окончательное удаление не выполнено: " + ex.Message;
            MessageBox.Show(ex.Message, "Удаление не выполнено", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsFileOperationRunning = false;
            ClearVisualGroupsAfterLibraryChange();
            await LoadQuarantineAsync();
            await LoadStatisticsAsync();
            await LoadEventsAsync();
        }
    }

    private async Task LoadQuarantineAsync()
    {
        var selectedId = SelectedQuarantineAction?.Id;
        var actions = await _database.GetQuarantineActionsAsync();
        QuarantineActions.Clear();
        foreach (var action in actions) QuarantineActions.Add(action);
        SelectedQuarantineAction = QuarantineActions.FirstOrDefault(x => x.Id == selectedId) ?? QuarantineActions.FirstOrDefault();

        var active = actions.Where(x => x.IsActive).ToList();
        var deleted = actions.Count(x => x.IsPermanentlyDeleted);
        var bytes = active.Sum(x => x.FileSize);
        QuarantineSummaryText = active.Count == 0
            ? $"Карантин пуст. История операций сохраняется. Окончательно удалено ранее: {deleted:N0}."
            : $"Сейчас в карантине: {active.Count:N0} файлов · {ByteFormatter.Format(bytes)}. Окончательное удаление по умолчанию выключено в настройках.";
    }

    private void ClearVisualGroupsAfterLibraryChange()
    {
        _personFilterId = null;
        _personFilterName = "";
        _eventFilterId = null;
        _eventFilterName = "";

        VisualDuplicateGroups.Clear();
        SelectedVisualDuplicateGroup = null;
        SelectedVisualDuplicateFile = null;
        VisualDuplicateSummaryText = "Каталог изменился. Нажмите «Из кэша» для быстрой перегруппировки или «Найти похожие» для полного анализа.";

        BurstGroups.Clear();
        SelectedBurstGroup = null;
        SelectedBurstFile = null;
        BurstSummaryText = "Каталог изменился. Нажмите «Из кэша» для быстрой перегруппировки серий или «Найти серии» для полного анализа.";

        PersonGroups.Clear();
        PersonFaces.Clear();
        SelectedPersonGroup = null;
        SelectedPersonFace = null;
        PeopleSummaryText = "Каталог изменился. Нажмите «Обновить» на вкладке «Люди»; изменённые фотографии будут повторно проиндексированы при следующем анализе лиц.";

        EventGroups.Clear();
        EventPhotos.Clear();
        SelectedEventGroup = null;
        SelectedEventPhoto = null;
        EventSummaryText = "Каталог изменился. Нажмите «Обновить» на вкладке «События» или перестройте автоматические события.";

        if (OrganizationPlan.Count > 0)
        {
            OrganizationPlan.Clear();
            SelectedOrganizationPlanItem = null;
            OrganizationSummaryText = "Каталог изменился — старый preview организации сброшен. Постройте его заново.";
            OrganizationProgressText = "";
        }
    }

    private PhotoQuery BuildQuery(int offset)
    {
        int? year = int.TryParse(SelectedYear, out var parsedYear) ? parsedYear : null;
        return new PhotoQuery
        {
            SearchText = SearchText,
            Year = year,
            Camera = SelectedCamera == "Все" ? "" : SelectedCamera,
            SourceFolder = SelectedSource?.Path ?? "",
            PersonId = _personFilterId,
            EventId = _eventFilterId,
            ReviewFilter = SelectedReviewFilter switch
            {
                ReviewNeeds => PhotoReviewFilter.NeedsReview,
                ReviewUntrustedDate => PhotoReviewFilter.UntrustedDate,
                ReviewWithoutEvent => PhotoReviewFilter.WithoutEvent,
                ReviewIndexError => PhotoReviewFilter.IndexError,
                _ => PhotoReviewFilter.All
            },
            SortOrder = SelectedPhotoSort switch
            {
                SortDateAscending => PhotoSortOrder.CaptureDateAscending,
                SortFileName => PhotoSortOrder.FileNameAscending,
                SortFullPath => PhotoSortOrder.FullPathAscending,
                _ => PhotoSortOrder.CaptureDateDescending
            },
            Limit = PageSize,
            Offset = offset
        };
    }

    private async Task ResetFiltersAsync()
    {
        SearchText = "";
        SelectedYear = "Все";
        SelectedCamera = "Все";
        SelectedReviewFilter = ReviewAll;
        SelectedPhotoSort = SortDateDescending;
        SelectedSource = null;
        _personFilterId = null;
        _personFilterName = "";
        _eventFilterId = null;
        _eventFilterName = "";
        await LoadPhotosAsync(reset: true);
    }

    private async Task LoadStatisticsAsync()
    {
        var s = await _database.GetStatisticsAsync();
        StatisticsText = $"Файлов: {s.TotalFiles:N0}\nОбъём: {ByteFormatter.Format(s.TotalBytes)}\n" +
                         $"С ошибками превью/метаданных: {s.ErrorFiles:N0}\n" +
                         $"Исчезли с диска после последнего полного скана: {s.MissingFiles:N0}";
    }

    private void ShowInExplorer()
    {
        if (SelectedPhoto is null) return;
        ShowPathInExplorer(SelectedPhoto.FullPath);
    }

    private void OpenPhoto()
    {
        if (SelectedPhoto is null) return;
        OpenPath(SelectedPhoto.FullPath);
    }

    private void ShowDuplicateInExplorer()
    {
        if (SelectedDuplicateFile is null) return;
        ShowPathInExplorer(SelectedDuplicateFile.FullPath);
    }

    private void OpenDuplicateFile()
    {
        if (SelectedDuplicateFile is null) return;
        OpenPath(SelectedDuplicateFile.FullPath);
    }

    private void ShowVisualInExplorer()
    {
        if (SelectedVisualDuplicateFile is null) return;
        ShowPathInExplorer(SelectedVisualDuplicateFile.FullPath);
    }

    private void OpenVisualFile()
    {
        if (SelectedVisualDuplicateFile is null) return;
        OpenPath(SelectedVisualDuplicateFile.FullPath);
    }

    private void ShowBurstInExplorer()
    {
        if (SelectedBurstFile is null) return;
        ShowPathInExplorer(SelectedBurstFile.FullPath);
    }

    private void OpenBurstFile()
    {
        if (SelectedBurstFile is null) return;
        OpenPath(SelectedBurstFile.FullPath);
    }

    private void ShowQuarantineInExplorer()
    {
        if (SelectedQuarantineAction is null) return;
        ShowPathInExplorer(SelectedQuarantineAction.CurrentPath);
    }

    private void OpenQuarantineFile()
    {
        if (SelectedQuarantineAction is null) return;
        OpenPath(SelectedQuarantineAction.CurrentPath);
    }

    private static void ShowPathInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private static void OpenPath(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private void RaiseScanCommands()
    {
        StartScanCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        QuarantineOtherCopiesCommand.RaiseCanExecuteChanged();
        FindVisualDuplicatesCommand.RaiseCanExecuteChanged();
        RefreshVisualDuplicatesCommand.RaiseCanExecuteChanged();
        FindBurstsCommand.RaiseCanExecuteChanged();
        RefreshBurstsCommand.RaiseCanExecuteChanged();
        AnalyzePeopleCommand.RaiseCanExecuteChanged();
        GroupPeopleCommand.RaiseCanExecuteChanged();
        RefreshPeopleCommand.RaiseCanExecuteChanged();
        AnalyzeEventsCommand.RaiseCanExecuteChanged();
        RefreshEventsCommand.RaiseCanExecuteChanged();
        BuildOrganizationPlanCommand.RaiseCanExecuteChanged();
        ExecuteOrganizationPlanCommand.RaiseCanExecuteChanged();
        UndoOrganizationMoveCommand.RaiseCanExecuteChanged();
        UndoQuarantineCommand.RaiseCanExecuteChanged();
        PermanentDeleteQuarantineCommand.RaiseCanExecuteChanged();
        EditPhotoCaptureDateCommand.RaiseCanExecuteChanged();
        ResetPhotoCaptureDateCommand.RaiseCanExecuteChanged();
    }

    private void RaiseViewerCommands()
    {
        ShowInExplorerCommand.RaiseCanExecuteChanged();
        OpenPhotoCommand.RaiseCanExecuteChanged();
        EditPhotoCaptureDateCommand.RaiseCanExecuteChanged();
        ResetPhotoCaptureDateCommand.RaiseCanExecuteChanged();
    }

    private void RaiseDuplicateCommands()
    {
        FindExactDuplicatesCommand.RaiseCanExecuteChanged();
        PauseDuplicateAnalysisCommand.RaiseCanExecuteChanged();
        ResumeDuplicateAnalysisCommand.RaiseCanExecuteChanged();
        StopDuplicateAnalysisCommand.RaiseCanExecuteChanged();
        RefreshDuplicatesCommand.RaiseCanExecuteChanged();
        QuarantineOtherCopiesCommand.RaiseCanExecuteChanged();
        ShowDuplicateFolderInExplorerCommand.RaiseCanExecuteChanged();
        ShowDuplicateFolderMatchInExplorerCommand.RaiseCanExecuteChanged();
        QuarantineDuplicateFolderPairSideCommand.RaiseCanExecuteChanged();
    }

    private void RaiseVisualCommands()
    {
        FindVisualDuplicatesCommand.RaiseCanExecuteChanged();
        RefreshVisualDuplicatesCommand.RaiseCanExecuteChanged();
        PauseVisualAnalysisCommand.RaiseCanExecuteChanged();
        ResumeVisualAnalysisCommand.RaiseCanExecuteChanged();
        StopVisualAnalysisCommand.RaiseCanExecuteChanged();
        AnalyzeSelectedQualityCommand.RaiseCanExecuteChanged();
        PauseQualityAnalysisCommand.RaiseCanExecuteChanged();
        ResumeQualityAnalysisCommand.RaiseCanExecuteChanged();
        StopQualityAnalysisCommand.RaiseCanExecuteChanged();
        ClearVisualReviewCommand.RaiseCanExecuteChanged();
        QuarantineMarkedVisualCommand.RaiseCanExecuteChanged();
    }

    private void RaiseBurstCommands()
    {
        FindBurstsCommand.RaiseCanExecuteChanged();
        RefreshBurstsCommand.RaiseCanExecuteChanged();
        PauseBurstAnalysisCommand.RaiseCanExecuteChanged();
        ResumeBurstAnalysisCommand.RaiseCanExecuteChanged();
        StopBurstAnalysisCommand.RaiseCanExecuteChanged();
        ShowBurstInExplorerCommand.RaiseCanExecuteChanged();
        OpenBurstFileCommand.RaiseCanExecuteChanged();
        AnalyzeSelectedBurstQualityCommand.RaiseCanExecuteChanged();
        ApplyBurstRecommendationCommand.RaiseCanExecuteChanged();
        MarkBurstOthersCommand.RaiseCanExecuteChanged();
        ClearBurstReviewCommand.RaiseCanExecuteChanged();
        QuarantineMarkedBurstCommand.RaiseCanExecuteChanged();
        PauseQualityAnalysisCommand.RaiseCanExecuteChanged();
        ResumeQualityAnalysisCommand.RaiseCanExecuteChanged();
        StopQualityAnalysisCommand.RaiseCanExecuteChanged();
    }

    private void RaisePeopleCommands()
    {
        AnalyzePeopleCommand.RaiseCanExecuteChanged();
        PausePeopleAnalysisCommand.RaiseCanExecuteChanged();
        ResumePeopleAnalysisCommand.RaiseCanExecuteChanged();
        StopPeopleAnalysisCommand.RaiseCanExecuteChanged();
        GroupPeopleCommand.RaiseCanExecuteChanged();
        RefreshPeopleCommand.RaiseCanExecuteChanged();
        RenamePersonCommand.RaiseCanExecuteChanged();
        AssignFaceToPersonCommand.RaiseCanExecuteChanged();
        MergePersonGroupCommand.RaiseCanExecuteChanged();
        DeletePersonGroupCommand.RaiseCanExecuteChanged();
        RemoveFaceFromPersonCommand.RaiseCanExecuteChanged();
        IgnoreFaceCommand.RaiseCanExecuteChanged();
        ShowPersonPhotosCommand.RaiseCanExecuteChanged();
        ShowPersonFaceInExplorerCommand.RaiseCanExecuteChanged();
        OpenPersonPhotoCommand.RaiseCanExecuteChanged();
        IgnorePersonGroupCommand.RaiseCanExecuteChanged();
        UndoLastFaceIgnoreCommand.RaiseCanExecuteChanged();
        SetPersonCoverCommand.RaiseCanExecuteChanged();
    }

    private void RaiseEventCommands()
    {
        AnalyzeEventsCommand.RaiseCanExecuteChanged();
        PauseEventAnalysisCommand.RaiseCanExecuteChanged();
        ResumeEventAnalysisCommand.RaiseCanExecuteChanged();
        StopEventAnalysisCommand.RaiseCanExecuteChanged();
        RefreshEventsCommand.RaiseCanExecuteChanged();
        RenameEventCommand.RaiseCanExecuteChanged();
        MergeEventCommand.RaiseCanExecuteChanged();
        ShowEventPhotosCommand.RaiseCanExecuteChanged();
        ShowEventPhotoInExplorerCommand.RaiseCanExecuteChanged();
        OpenEventPhotoCommand.RaiseCanExecuteChanged();
        SetEventCoverCommand.RaiseCanExecuteChanged();
        EditEventNotesCommand.RaiseCanExecuteChanged();
        SplitEventCommand.RaiseCanExecuteChanged();
    }

    private void RaiseBurstViewerCommands()
    {
        ShowBurstInExplorerCommand.RaiseCanExecuteChanged();
        OpenBurstFileCommand.RaiseCanExecuteChanged();
        MarkBurstOthersCommand.RaiseCanExecuteChanged();
    }

    private void RaiseVisualViewerCommands()
    {
        ShowVisualInExplorerCommand.RaiseCanExecuteChanged();
        OpenVisualFileCommand.RaiseCanExecuteChanged();
    }

    private void RaiseDuplicateViewerCommands()
    {
        ShowDuplicateInExplorerCommand.RaiseCanExecuteChanged();
        OpenDuplicateFileCommand.RaiseCanExecuteChanged();
        QuarantineOtherCopiesCommand.RaiseCanExecuteChanged();
    }


    private void RaiseOrganizationCommands()
    {
        BuildOrganizationPlanCommand.RaiseCanExecuteChanged();
        SelectTrustedOrganizationCommand.RaiseCanExecuteChanged();
        ClearOrganizationSelectionCommand.RaiseCanExecuteChanged();
        ExecuteOrganizationPlanCommand.RaiseCanExecuteChanged();
        PauseOrganizationCommand.RaiseCanExecuteChanged();
        ResumeOrganizationCommand.RaiseCanExecuteChanged();
        StopOrganizationCommand.RaiseCanExecuteChanged();
        RefreshOrganizationHistoryCommand.RaiseCanExecuteChanged();
        UndoOrganizationMoveCommand.RaiseCanExecuteChanged();
        ShowOrganizationSourceCommand.RaiseCanExecuteChanged();
        ShowOrganizationTargetCommand.RaiseCanExecuteChanged();
        ShowOrganizationMoveCommand.RaiseCanExecuteChanged();
    }

    private void RaiseQuarantineCommands()
    {
        RefreshQuarantineCommand.RaiseCanExecuteChanged();
        UndoQuarantineCommand.RaiseCanExecuteChanged();
        PermanentDeleteQuarantineCommand.RaiseCanExecuteChanged();
        ShowQuarantineInExplorerCommand.RaiseCanExecuteChanged();
        OpenQuarantineFileCommand.RaiseCanExecuteChanged();
    }

    private sealed class DuplicateFolderAccumulator
    {
        public DuplicateFolderAccumulator(string folderPath) => FolderPath = folderPath;
        public string FolderPath { get; }
        public int ExactSetCount { get; set; }
        public int DuplicateFileCount { get; set; }
        public int RemovableFromFolderCount { get; set; }
        public long RemovableFromFolderBytes { get; set; }
        public int RemovableElsewhereCount { get; set; }
        public long RemovableElsewhereBytes { get; set; }
        public int InternalExtraCount { get; set; }
        public int RequiredKeeperCount { get; set; }
        public Dictionary<string, DuplicateFolderMatchAccumulator> Matches { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DuplicateFolderMatchAccumulator
    {
        public DuplicateFolderMatchAccumulator(string otherFolderPath) => OtherFolderPath = otherFolderPath;
        public string OtherFolderPath { get; }
        public int SharedSetCount { get; set; }
        public int SelectedCopyCount { get; set; }
        public long SelectedBytes { get; set; }
        public int OtherCopyCount { get; set; }
        public long OtherBytes { get; set; }
    }

    private static string TruncateMiddle(string text, int max)
    {
        if (text.Length <= max) return text;
        var half = (max - 3) / 2;
        return text[..half] + "..." + text[^half..];
    }
}
