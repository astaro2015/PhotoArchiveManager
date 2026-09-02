using PhotoArchiveManager.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoArchiveManager;

public partial class CompareWindow : Window, INotifyPropertyChanged
{
    private readonly List<ScrollViewer> _viewers = new();
    private bool _syncingScroll;
    private double _zoom = 1.0;
    private ScrollViewer? _dragViewer;
    private Point _dragStart;
    private double _dragHorizontalOffset;
    private double _dragVerticalOffset;

    public IReadOnlyList<VisualDuplicateFileItem> Photos { get; }
    public string HeaderText { get; }

    public double Zoom
    {
        get => _zoom;
        set
        {
            var normalized = Math.Clamp(value, 0.05, 4.0);
            if (Math.Abs(_zoom - normalized) < 0.0001) return;
            _zoom = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomDisplay));
        }
    }

    public string ZoomDisplay => $"{Zoom * 100:0}%";

    public CompareWindow(VisualDuplicateGroupItem group)
        : this(group.Files, group.Files.Count > 8
            ? $"В группе {group.Files.Count} файлов; показаны первые 8. После Quality Score рекомендуемый кадр идёт первым."
            : $"{group.Files.Count} файлов в группе. После Quality Score рекомендуемый кадр идёт первым.")
    {
    }

    public CompareWindow(IReadOnlyList<VisualDuplicateFileItem> files, string headerText)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        Photos = files.Take(8).ToList();
        HeaderText = files.Count > 8 ? headerText + " Показаны первые 8." : headerText;
        DataContext = this;
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitToFirstImage));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void ImageScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer && !_viewers.Contains(viewer))
            _viewers.Add(viewer);
    }

    private void ImageScrollViewer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer)
            _viewers.Remove(viewer);
    }

    private void ImageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || sender is not ScrollViewer source) return;
        if (Math.Abs(e.HorizontalChange) < 0.001 && Math.Abs(e.VerticalChange) < 0.001) return;
        SynchronizeScroll(source);
    }

    private void SynchronizeScroll(ScrollViewer source)
    {
        _syncingScroll = true;
        try
        {
            var horizontalFraction = source.ScrollableWidth <= 0 ? 0 : source.HorizontalOffset / source.ScrollableWidth;
            var verticalFraction = source.ScrollableHeight <= 0 ? 0 : source.VerticalOffset / source.ScrollableHeight;
            foreach (var viewer in _viewers)
            {
                if (ReferenceEquals(viewer, source)) continue;
                viewer.ScrollToHorizontalOffset(horizontalFraction * viewer.ScrollableWidth);
                viewer.ScrollToVerticalOffset(verticalFraction * viewer.ScrollableHeight);
            }
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetZoomAroundCurrentPosition(e.Delta > 0 ? Zoom * 1.12 : Zoom / 1.12);
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoomAroundCurrentPosition(Zoom * 1.20);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoomAroundCurrentPosition(Zoom / 1.20);
    private void Zoom100_Click(object sender, RoutedEventArgs e) => SetZoomAroundCurrentPosition(1.0);
    private void Fit_Click(object sender, RoutedEventArgs e) => FitToFirstImage();

    private void SetZoomAroundCurrentPosition(double newZoom)
    {
        var source = _viewers.FirstOrDefault();
        var horizontalFraction = source is null || source.ScrollableWidth <= 0 ? 0.5 : source.HorizontalOffset / source.ScrollableWidth;
        var verticalFraction = source is null || source.ScrollableHeight <= 0 ? 0.5 : source.VerticalOffset / source.ScrollableHeight;
        Zoom = newZoom;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _syncingScroll = true;
            try
            {
                foreach (var viewer in _viewers)
                {
                    viewer.ScrollToHorizontalOffset(horizontalFraction * viewer.ScrollableWidth);
                    viewer.ScrollToVerticalOffset(verticalFraction * viewer.ScrollableHeight);
                }
            }
            finally { _syncingScroll = false; }
        }));
    }

    private void FitToFirstImage()
    {
        var viewer = _viewers.FirstOrDefault();
        if (viewer is null || Photos.Count == 0) return;
        var first = Photos[0];
        if (first.DisplayPixelWidth <= 0 || first.DisplayPixelHeight <= 0 || viewer.ViewportWidth <= 0 || viewer.ViewportHeight <= 0) return;

        var fit = Math.Min(viewer.ViewportWidth / first.DisplayPixelWidth, viewer.ViewportHeight / first.DisplayPixelHeight);
        Zoom = Math.Clamp(fit * 0.97, 0.05, 1.0);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _syncingScroll = true;
            try
            {
                foreach (var item in _viewers)
                {
                    item.ScrollToHorizontalOffset(0);
                    item.ScrollToVerticalOffset(0);
                }
            }
            finally { _syncingScroll = false; }
        }));
    }

    private void ImageScrollViewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        if (e.OriginalSource is DependencyObject origin && FindVisualParent<ScrollBar>(origin) is not null) return;
        _dragViewer = viewer;
        _dragStart = e.GetPosition(viewer);
        _dragHorizontalOffset = viewer.HorizontalOffset;
        _dragVerticalOffset = viewer.VerticalOffset;
        viewer.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Hand;
        e.Handled = true;
    }

    private void ImageScrollViewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragViewer is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(_dragViewer);
        _dragViewer.ScrollToHorizontalOffset(_dragHorizontalOffset - (current.X - _dragStart.X));
        _dragViewer.ScrollToVerticalOffset(_dragVerticalOffset - (current.Y - _dragStart.Y));
        e.Handled = true;
    }

    private void ImageScrollViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragViewer is null) return;
        _dragViewer.ReleaseMouseCapture();
        _dragViewer = null;
        Mouse.OverrideCursor = null;
        e.Handled = true;
    }
    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is T match) return match;
            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch (InvalidOperationException)
            {
                current = LogicalTreeHelper.GetParent(current);
            }
        }
        return null;
    }

}
