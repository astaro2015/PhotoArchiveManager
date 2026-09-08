using PhotoArchiveManager.Infrastructure;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PhotoArchiveManager.Models;
using PhotoArchiveManager.Services;
using Microsoft.Win32;
using PhotoArchiveManager.ViewModels;

namespace PhotoArchiveManager;

public partial class MainWindow : Window
{
    private Point _eventGroupDragStart;
    private EventGroupItem? _eventGroupDragCandidate;
    private const string EventGroupDragFormat = "PhotoArchiveManager.EventGroupDragData";
    private Point _eventPhotoDragStart;
    private PhotoItem? _eventPhotoDragCandidate;
    private long[] _eventPhotoDragFileIds = [];
    private const string EventPhotoDragFormat = "PhotoArchiveManager.EventPhotoDragData";
    private Point _personFaceDragStart;
    private FaceItem? _personFaceDragCandidate;
    private const string PersonFaceDragFormat = "PhotoArchiveManager.PersonFaceDragData";

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, _) => ViewModel.RequestStop();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += (_, _) => SelectEventInTree(ViewModel.SelectedEventGroup?.Id);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedEventGroup))
                Dispatcher.BeginInvoke(new Action(() => SelectEventInTree(ViewModel.SelectedEventGroup?.Id)));
        };
    }


    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Global shortcuts are intentionally limited to non-destructive actions.
        // Physical file operations (quarantine/permanent delete/organization move)
        // must still be initiated explicitly through the UI and confirmations.
        if (key == Key.F1 && modifiers == ModifierKeys.None)
        {
            Help_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (key == Key.F5 && modifiers == ModifierKeys.None)
        {
            ExecuteIfPossible(ViewModel.RefreshCommand);
            e.Handled = true;
            return;
        }
        if (key == Key.F9 && modifiers == ModifierKeys.None)
        {
            ExecuteIfPossible(ViewModel.StartScanCommand);
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && key == Key.O)
        {
            AddFolder_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && TryGetSectionIndex(key, out var sectionIndex))
        {
            ViewModel.SelectedMainTabIndex = sectionIndex;
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && key == Key.F)
        {
            FocusCurrentSearch();
            e.Handled = true;
            return;
        }

        // Enter in the two search boxes performs the expected search action.
        if (key == Key.Enter && modifiers == ModifierKeys.None && ReferenceEquals(Keyboard.FocusedElement, LibrarySearchBox))
        {
            ExecuteIfPossible(ViewModel.ApplyFiltersCommand);
            e.Handled = true;
            return;
        }
        // Do not steal editing/navigation gestures while the user is typing in a field.
        if (IsTextEditingControl(Keyboard.FocusedElement)) return;

        if (key == Key.Space && modifiers == ModifierKeys.None && OpenViewerForCurrentSelection())
        {
            e.Handled = true;
            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.D && ViewModel.SelectedMainTabIndex == 0)
        {
            BatchDate_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.E && ViewModel.SelectedMainTabIndex == 0)
        {
            BatchEvent_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C)
        {
            if (ViewModel.SelectedMainTabIndex == 2)
            {
                CompareVisual_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ViewModel.SelectedMainTabIndex == 3)
            {
                CompareBurst_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    private void FocusCurrentSearch()
    {
        if (ViewModel.SelectedMainTabIndex != 0)
            ViewModel.SelectedMainTabIndex = 0;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            LibrarySearchBox.Focus();
            LibrarySearchBox.SelectAll();
        }));
    }


    private static bool ExecuteIfPossible(System.Windows.Input.ICommand? command)
    {
        if (command is null || !command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }

    private static bool IsTextEditingControl(IInputElement? focusedElement)
        => focusedElement is TextBoxBase or PasswordBox ||
           focusedElement is ComboBox comboBox && comboBox.IsEditable;

    private static bool TryGetSectionIndex(Key key, out int index)
    {
        index = key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            Key.D0 or Key.NumPad0 => 9,
            _ => -1
        };
        return index >= 0;
    }

    private void CompareVisual_Click(object sender, RoutedEventArgs e)
    {
        var group = ViewModel.SelectedVisualDuplicateGroup;
        if (group is null || group.Files.Count < 2)
        {
            MessageBox.Show(this, "Сначала выберите группу минимум из двух похожих фотографий.", "Сравнение", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new CompareWindow(group);
        window.ShowDialog();
    }

    private void CompareBurst_Click(object sender, RoutedEventArgs e)
    {
        var group = ViewModel.SelectedBurstGroup;
        if (group is null || group.Files.Count < 2)
        {
            MessageBox.Show(this, "Сначала выберите серию минимум из двух фотографий.", "Сравнение серии", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new CompareWindow(group.Files, $"Серия {group.DateDisplay} · {group.TimeRangeDisplay} · {group.FileCount} кадров. Звёздочками отмечены технические рекомендации PAM.");
        window.ShowDialog();
    }

    private void TimelineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TimelineMonthItem month)
            ViewModel.SelectedTimelineMonth = month;
    }

    private void PhotoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBox listBox) return;
        if (e.OriginalSource is not DependencyObject source) return;

        // Resolve the actual row under the mouse before reading ViewModel.Selected*.
        // This avoids opening the previously selected photo if the second click arrives
        // before SelectedItem binding has propagated to the view-model.
        if (ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem row) return;
        listBox.SelectedItem = row.DataContext;
        listBox.ScrollIntoView(row.DataContext);

        e.Handled = true;
        OpenViewerForCurrentSelection(GetViewerItemId(row.DataContext));
    }

    private void ListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem row) return;

        // Explorer-like behavior: right-clicking inside an existing multi-selection keeps the
        // whole selection. Right-clicking a different row makes only that row current.
        var wasSelected = row.IsSelected;
        if (!wasSelected)
        {
            if (listBox.SelectionMode != SelectionMode.Single)
                listBox.SelectedItems.Clear();
            row.IsSelected = true;
        }

        var item = row.DataContext;
        switch (listBox.Name)
        {
            case nameof(LibraryPhotosList) when item is PhotoItem photo:
                ViewModel.SelectedPhoto = photo;
                break;
            case nameof(DuplicateFilesList) when item is DuplicateFileItem duplicate:
                ViewModel.SelectedDuplicateFile = duplicate;
                break;
            case nameof(VisualFilesList) when item is VisualDuplicateFileItem visual:
                ViewModel.SelectedVisualDuplicateFile = visual;
                break;
            case nameof(BurstFilesList) when item is VisualDuplicateFileItem burst:
                ViewModel.SelectedBurstFile = burst;
                break;
            case nameof(PersonGroupsList) when item is PersonGroupItem person:
                // Do NOT assign SelectedPersonGroup when right-clicking one row of an existing
                // multi-selection: because SelectedItem is two-way bound, that assignment may
                // collapse SelectedItems to a single group. Keep the batch selection intact and
                // remember only which selected row the user invoked the context menu on; that
                // row becomes the preferred keeper/target in the merge picker.
                ViewModel.SetPreferredPersonMergeTarget(person);
                if (!wasSelected || listBox.SelectedItems.Count <= 1)
                    ViewModel.SelectedPersonGroup = person;
                break;
            case nameof(PersonFacesList) when item is FaceItem face:
                ViewModel.SelectedPersonFace = face;
                break;
            case nameof(EventPhotosList) when item is PhotoItem eventPhoto:
                ViewModel.SelectedEventPhoto = eventPhoto;
                break;
            case nameof(TimelinePhotosList) when item is PhotoItem timelinePhoto:
                ViewModel.SelectedTimelinePhoto = timelinePhoto;
                break;
            case nameof(QuarantineActionsList) when item is QuarantineActionItem quarantine:
                ViewModel.SelectedQuarantineAction = quarantine;
                break;
        }

        listBox.ScrollIntoView(item);
    }

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        var row = FindAncestor<DataGridRow>(source);
        if (row is null) return;

        grid.SelectedItem = row.Item;
        switch (row.Item)
        {
            case OrganizationPlanItem plan:
                ViewModel.SelectedOrganizationPlanItem = plan;
                break;
            case OrganizationActionItem move:
                ViewModel.SelectedOrganizationMove = move;
                break;
        }
        row.Focus();
    }

    private void EventGroupsTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var item = FindAncestor<TreeViewItem>(source);
        if (item is null) return;

        item.IsSelected = true;
        item.Focus();
        if (item.DataContext is EventGroupItem eventGroup)
            ViewModel.SelectedEventGroup = eventGroup;
    }

    private void EventGroupsTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Year headers have no event-level actions. Suppress the menu there instead of
        // accidentally applying commands to the previously selected event.
        if (EventGroupsTree.SelectedItem is not EventGroupItem)
            e.Handled = true;
    }

    private static long? GetViewerItemId(object? item) => item switch
    {
        PhotoItem p => p.Id,
        DuplicateFileItem p => p.Id,
        VisualDuplicateFileItem p => p.Id,
        FaceItem p => p.FileId,
        _ => null
    };

    private void OpenViewer_Click(object sender, RoutedEventArgs e) => OpenViewerForCurrentSelection();

    private bool OpenViewerForCurrentSelection(long? preferredId = null)
    {
        IReadOnlyList<ViewerPhotoItem> items;
        long? selectedId;

        switch (ViewModel.SelectedMainTabIndex)
        {
            case 0:
                items = ViewModel.Photos.Select(ViewerPhotoItem.FromPhoto).ToList();
                selectedId = preferredId ?? ViewModel.SelectedPhoto?.Id;
                break;
            case 1:
                items = (ViewModel.SelectedDuplicateGroup?.Files ?? Array.Empty<DuplicateFileItem>())
                    .Select(ToViewerItem).ToList();
                selectedId = preferredId ?? ViewModel.SelectedDuplicateFile?.Id;
                break;
            case 2:
                items = (ViewModel.SelectedVisualDuplicateGroup?.Files ?? Array.Empty<VisualDuplicateFileItem>())
                    .Select(ToViewerItem).ToList();
                selectedId = preferredId ?? ViewModel.SelectedVisualDuplicateFile?.Id;
                break;
            case 3:
                items = (ViewModel.SelectedBurstGroup?.Files ?? Array.Empty<VisualDuplicateFileItem>())
                    .Select(ToViewerItem).ToList();
                selectedId = preferredId ?? ViewModel.SelectedBurstFile?.Id;
                break;
            case 4:
                items = ViewModel.PersonFaces
                    .GroupBy(x => x.FileId)
                    .Select(x => x.First())
                    .Select(ToViewerItem).ToList();
                selectedId = preferredId ?? ViewModel.SelectedPersonFace?.FileId;
                break;
            case 5:
                items = ViewModel.EventPhotos.Select(ViewerPhotoItem.FromPhoto).ToList();
                selectedId = preferredId ?? ViewModel.SelectedEventPhoto?.Id;
                break;
            case 6:
                items = ViewModel.TimelinePhotos.Select(ViewerPhotoItem.FromPhoto).ToList();
                selectedId = preferredId ?? ViewModel.SelectedTimelinePhoto?.Id;
                break;
            default:
                return false;
        }

        items = items.Where(x => File.Exists(x.FullPath)).ToList();
        if (items.Count == 0) return false;
        var index = selectedId.HasValue ? Math.Max(0, items.ToList().FindIndex(x => x.Id == selectedId.Value)) : 0;
        var window = new PhotoViewerWindow(items, index) { Owner = this };
        window.ShowDialog();
        return true;
    }

    private static ViewerPhotoItem ToViewerItem(DuplicateFileItem p) => new()
    {
        Id = p.Id,
        FullPath = p.FullPath,
        FileName = p.FileName,
        ThumbnailPath = p.ThumbnailPath,
        CaptureDateDisplay = p.CaptureDateDisplay,
        Details = $"{p.DimensionsDisplay} · {p.FileSizeDisplay} · {p.CameraDisplay}"
    };

    private static ViewerPhotoItem ToViewerItem(VisualDuplicateFileItem p) => new()
    {
        Id = p.Id,
        FullPath = p.FullPath,
        FileName = p.FileName,
        ThumbnailPath = p.ThumbnailPath,
        CaptureDateDisplay = p.CaptureDateDisplay,
        Details = $"{p.DimensionsDisplay} · {p.FileSizeDisplay} · {p.CameraDisplay} · Quality {p.QualityDisplay}"
    };

    private static ViewerPhotoItem ToViewerItem(FaceItem p) => new()
    {
        Id = p.FileId,
        FullPath = p.FullPath,
        FileName = p.FileName,
        ThumbnailPath = p.PhotoThumbnailPath,
        CaptureDateDisplay = p.CaptureDateDisplay,
        Details = $"{p.GroupDisplay} · {p.QualityDisplay}"
    };

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку с фотографиями",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await ViewModel.AddSourceFolderAsync(dialog.FolderName);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Add source folder failed", ex);
            MessageBox.Show(this, ex.Message, "Не удалось добавить источник", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        var source = ViewModel.SelectedSource;
        if (source is null)
        {
            MessageBox.Show(this, "Сначала выберите источник в списке слева.", "Источники", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ViewModel.CanManageSelectedSource)
        {
            MessageBox.Show(this, "Дождитесь окончания текущего анализа/операции или остановите его.", "Источники", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "Убрать источник из PAM?\n\n" + source.Path +
            "\n\nДА — убрать источник И забыть его активные записи/анализы в каталоге PAM.\n" +
            "      Файлы, уже находящиеся в карантине, и их Undo-журнал сохранятся и не мешают закрытию источника.\n" +
            "НЕТ — убрать только из списка источников, но оставить уже проиндексированные записи в каталоге.\n\n" +
            "Ни один обычный файл или папка на диске НЕ будет удалён.\n\nОТМЕНА — ничего не делать.",
            "Удаление источника из PAM",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel) return;
        try
        {
            await ViewModel.RemoveSelectedSourceAsync(answer == MessageBoxResult.Yes);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось убрать источник", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ReplaceSource_Click(object sender, RoutedEventArgs e)
    {
        var source = ViewModel.SelectedSource;
        if (source is null)
        {
            MessageBox.Show(this, "Сначала выберите источник, путь которого нужно заменить.", "Источники", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ViewModel.CanManageSelectedSource)
        {
            MessageBox.Show(this, "Дождитесь окончания текущего анализа/операции или остановите его.", "Источники", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Где теперь находится эта папка?",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(this,
            "PAM заменит только путь в своём каталоге:\n\n" + source.Path + "\n→\n" + dialog.FolderName +
            "\n\nФайлы на диске перемещаться не будут. Хэши, Quality, лица и другие уже рассчитанные данные будут сохранены. Продолжить?",
            "Заменить путь источника",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await ViewModel.ReplaceSelectedSourceAsync(dialog.FolderName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось заменить путь", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChooseOrganizationRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите корневую папку нового организованного архива",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
            ViewModel.OrganizationDestinationRoot = dialog.FolderName;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var window = new HelpWindow { Owner = this };
        window.ShowDialog();
    }

    private PhotoItem[] GetSelectedLibraryPhotos()
    {
        var selected = LibraryPhotosList.SelectedItems.Cast<PhotoItem>().Where(x => x.Id > 0).GroupBy(x => x.Id).Select(x => x.First()).ToArray();
        if (selected.Length == 0 && ViewModel.SelectedPhoto is { Id: > 0 } single) selected = [single];
        return selected;
    }

    private async void BatchDate_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedLibraryPhotos();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Выделите одну или несколько фотографий. Ctrl/Shift — множественное выделение.", "Пакетная дата", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var initial = StoredDateTime.TryParse(selected[0].CaptureDate, out var parsed) ? parsed : DateTime.Today;
        var dialog = new BatchCaptureDateWindow(selected.Length, initial) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await ViewModel.ApplyBatchCaptureDateAsync(selected, dialog.CaptureDate, dialog.PreserveExistingTime);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось изменить даты", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BatchEvent_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedLibraryPhotos();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Выделите одну или несколько фотографий. Ctrl/Shift — множественное выделение.", "Назначить событие", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var events = ViewModel.EventGroups.ToList();
        if (events.Count == 0)
        {
            MessageBox.Show(this, "Событий пока нет. Сначала постройте события в разделе «События», затем при необходимости назовите их.", "Назначить событие", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new BatchEventAssignmentWindow(selected.Length, events) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedEvent is null) return;
        var answer = MessageBox.Show(this,
            $"Назначить {selected.Length:N0} выбранных фото событию «{dialog.SelectedEvent.DisplayName}»?\n\nМеняется только каталог SQLite. Файлы не перемещаются и не изменяются.",
            "Назначить событие", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await ViewModel.AssignPhotosToEventAsync(selected, dialog.SelectedEvent.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось назначить событие", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportCatalog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт каталога Photo Archive Manager",
            Filter = "JSON (*.json)|*.json|CSV UTF-8 (*.csv)|*.csv",
            FilterIndex = 1,
            AddExtension = true,
            FileName = $"PAM_catalog_{DateTime.Now:yyyyMMdd_HHmm}.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var json = dialog.FilterIndex == 1 || string.Equals(Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase);
        try
        {
            await ViewModel.ExportCatalogAsync(dialog.FileName, json);
            MessageBox.Show(this, "Экспорт завершён:\n\n" + dialog.FileName, "Экспорт каталога", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PersonGroupsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not ListBox listBox) return;
        viewModel.UpdateSelectedPersonGroups(listBox.SelectedItems.Cast<PersonGroupItem>());
    }

    private void PersonGroupsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.A)
        {
            listBox.SelectAll();
            e.Handled = true;
        }
    }

    private void PersonFacesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _personFaceDragStart = e.GetPosition(PersonFacesList);
        _personFaceDragCandidate = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as FaceItem;
    }

    private void PersonFacesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _personFaceDragCandidate is null || !ViewModel.CanPerformCatalogReview) return;
        var current = e.GetPosition(PersonFacesList);
        if (Math.Abs(current.X - _personFaceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _personFaceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var face = _personFaceDragCandidate;
        var data = new DataObject();
        data.SetData(PersonFaceDragFormat, new PersonFaceDragData(face.FaceId, face.PersonId, face.FileName, face.GroupDisplay));
        try { DragDrop.DoDragDrop(PersonFacesList, data, DragDropEffects.Move); }
        finally { _personFaceDragCandidate = null; }
    }

    private void PersonGroupsList_PreviewDragOver(object sender, DragEventArgs e)
    {
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PersonGroupItem;
        var payload = e.Data.GetDataPresent(PersonFaceDragFormat) ? e.Data.GetData(PersonFaceDragFormat) as PersonFaceDragData : null;
        e.Effects = ViewModel.CanPerformCatalogReview && target is { Id: > 0 } && payload is not null && target.Id != payload.SourcePersonId
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void PersonGroupsList_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanPerformCatalogReview || !e.Data.GetDataPresent(PersonFaceDragFormat)) return;
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as PersonGroupItem;
        var payload = e.Data.GetData(PersonFaceDragFormat) as PersonFaceDragData;
        if (target is not { Id: > 0 } || payload is null || target.Id == payload.SourcePersonId) return;
        var face = ViewModel.PersonFaces.FirstOrDefault(x => x.FaceId == payload.FaceId);
        if (face is null) return;
        var answer = MessageBox.Show(this,
            $"Перенести лицо из фото «{payload.FileName}»\nиз группы «{payload.SourceGroupName}»\nв «{target.DisplayName}»?\n\nМеняется только каталог лиц PAM; фотография не изменяется.",
            "Перенос лица", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try { await ViewModel.AssignFaceToPersonAsync(face, target.Id); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось перенести лицо", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void EventPhotosList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _eventPhotoDragStart = e.GetPosition(EventPhotosList);
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _eventPhotoDragCandidate = item?.DataContext as PhotoItem;
        _eventPhotoDragFileIds = [];

        if (_eventPhotoDragCandidate is not { Id: > 0 } candidate) return;

        // Capture the selection before WPF processes the mouse-down. With SelectionMode=Extended,
        // clicking an already selected row without Ctrl/Shift normally collapses SelectedItems to
        // that single row before PreviewMouseMove fires. A drag must preserve the user's existing
        // multi-selection, so snapshot it here while it is still intact.
        if (item?.IsSelected == true)
        {
            _eventPhotoDragFileIds = EventPhotosList.SelectedItems
                .Cast<PhotoItem>()
                .Where(x => x.Id > 0)
                .Select(x => x.Id)
                .Distinct()
                .ToArray();
        }

        // Dragging a row that was not part of the current selection is intentionally a single-photo drag.
        if (_eventPhotoDragFileIds.Length == 0)
            _eventPhotoDragFileIds = [candidate.Id];
    }

    private void EventPhotosList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _eventPhotoDragCandidate is null) return;
        if (!ViewModel.CanMoveEventPhotos) return;

        var current = e.GetPosition(EventPhotosList);
        if (Math.Abs(current.X - _eventPhotoDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _eventPhotoDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var sourceEvent = ViewModel.SelectedEventGroup;
        if (sourceEvent is null || sourceEvent.Id <= 0) return;

        var ids = _eventPhotoDragFileIds.Where(x => x > 0).Distinct().ToArray();
        if (ids.Length == 0) return;

        var payload = new EventPhotoDragData(sourceEvent.Id, sourceEvent.DisplayName, ids);
        var data = new DataObject();
        data.SetData(EventPhotoDragFormat, payload);
        try
        {
            DragDrop.DoDragDrop(EventPhotosList, data, DragDropEffects.Move);
        }
        finally
        {
            _eventPhotoDragCandidate = null;
            _eventPhotoDragFileIds = [];
        }
    }

    private void EventGroupsTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _eventGroupDragStart = e.GetPosition(EventGroupsTree);
        _eventGroupDragCandidate = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as EventGroupItem;
    }

    private void EventGroupsTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _eventGroupDragCandidate is not { Id: > 0 } source) return;
        if (!ViewModel.CanMoveEventPhotos) return;

        var current = e.GetPosition(EventGroupsTree);
        if (Math.Abs(current.X - _eventGroupDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _eventGroupDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject();
        data.SetData(EventGroupDragFormat, new EventGroupDragData(source.Id, source.DisplayName, source.PhotoCount));
        try
        {
            DragDrop.DoDragDrop(EventGroupsTree, data, DragDropEffects.Move);
        }
        finally
        {
            _eventGroupDragCandidate = null;
        }
    }

    private void EventGroupsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is EventGroupItem selected) ViewModel.SelectedEventGroup = selected;
    }

    private void SelectEventInTree(long? eventId)
    {
        if (!eventId.HasValue || EventGroupsTree.Items.Count == 0) return;
        EventGroupsTree.UpdateLayout();
        foreach (var year in ViewModel.EventYears)
        {
            var match = year.Events.FirstOrDefault(x => x.Id == eventId.Value);
            if (match is null) continue;
            if (EventGroupsTree.ItemContainerGenerator.ContainerFromItem(year) is not TreeViewItem yearItem) return;
            yearItem.IsExpanded = true;
            yearItem.UpdateLayout();
            if (yearItem.ItemContainerGenerator.ContainerFromItem(match) is TreeViewItem eventItem)
            {
                eventItem.IsSelected = true;
                eventItem.BringIntoView();
            }
            return;
        }
    }

    private void EventGroupsTree_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanMoveEventPhotos)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as EventGroupItem;
        if (target is not { Id: > 0 })
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(EventGroupDragFormat))
        {
            var groupPayload = e.Data.GetData(EventGroupDragFormat) as EventGroupDragData;
            e.Effects = groupPayload is not null && target.Id != groupPayload.SourceEventId
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(EventPhotoDragFormat))
        {
            var photoPayload = e.Data.GetData(EventPhotoDragFormat) as EventPhotoDragData;
            e.Effects = photoPayload is not null && target.Id != photoPayload.SourceEventId
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void EventGroupsTree_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanMoveEventPhotos) return;
        var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as EventGroupItem;
        if (target is not { Id: > 0 }) return;

        if (e.Data.GetDataPresent(EventGroupDragFormat))
        {
            var payload = e.Data.GetData(EventGroupDragFormat) as EventGroupDragData;
            if (payload is null || target.Id == payload.SourceEventId) return;

            var answer = MessageBox.Show(this,
                $"Объединить событие\n«{payload.SourceEventName}» ({payload.PhotoCount:N0} фото)\nс событием\n«{target.DisplayName}» ({target.PhotoCount:N0} фото)?\n\n" +
                $"Останется событие «{target.DisplayName}»: его имя и обложка имеют приоритет. " +
                "Фотографии на диске не перемещаются и не изменяются — меняется только каталог событий PAM (SQLite).",
                "Объединить события",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                await ViewModel.MergeEventIntoAsync(payload.SourceEventId, target.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Не удалось объединить события", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        if (!e.Data.GetDataPresent(EventPhotoDragFormat)) return;
        var photoPayload = e.Data.GetData(EventPhotoDragFormat) as EventPhotoDragData;
        if (photoPayload is null || target.Id == photoPayload.SourceEventId) return;

        var countText = photoPayload.FileIds.Length == 1 ? "1 фотографию" : $"{photoPayload.FileIds.Length:N0} фото";
        var moveAnswer = MessageBox.Show(this,
            $"Перенести {countText} из события\n«{photoPayload.SourceEventName}»\nв событие\n«{target.DisplayName}»?\n\n" +
            "Изменится только каталог событий PAM (SQLite). Сами фотографии не перемещаются, не переименовываются и не изменяются. " +
            "Оба затронутых события будут закреплены как пользовательские.",
            "Перенос фото между событиями",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (moveAnswer != MessageBoxResult.Yes) return;

        try
        {
            await ViewModel.MoveEventPhotosAsync(photoPayload.SourceEventId, target.Id, photoPayload.FileIds);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось перенести фото", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current switch
            {
                Visual visual => VisualTreeHelper.GetParent(visual),
                FrameworkContentElement content => content.Parent,
                _ => null
            };
        }
        return null;
    }

    private sealed record PersonFaceDragData(long FaceId, long? SourcePersonId, string FileName, string SourceGroupName);
    private sealed record EventGroupDragData(long SourceEventId, string SourceEventName, int PhotoCount);
    private sealed record EventPhotoDragData(long SourceEventId, string SourceEventName, long[] FileIds);

}
