using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoArchiveManager.Models;
using PhotoArchiveManager.Services;

namespace PhotoArchiveManager;

public partial class PhotoViewerWindow : Window
{
    private readonly IReadOnlyList<ViewerPhotoItem> _items;
    private readonly MetadataService _metadata = new();
    private int _index;
    private BitmapSource? _bitmap;
    private double _zoom = 1.0;
    private bool _fitMode = true;
    private Point _panStart;
    private double _panHorizontal;
    private double _panVertical;
    private bool _panning;
    private bool _filmstripVisible = true;
    private bool _infoVisible = true;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public PhotoViewerWindow(IReadOnlyList<ViewerPhotoItem> items, int initialIndex = 0)
    {
        InitializeComponent();
        _items = items.Where(x => !string.IsNullOrWhiteSpace(x.FullPath)).ToList();
        _index = _items.Count == 0 ? 0 : Math.Clamp(initialIndex, 0, _items.Count - 1);
        Filmstrip.ItemsSource = _items;
        Loaded += (_, _) => ShowCurrent();
    }

    private void ShowCurrent()
    {
        if (_items.Count == 0)
        {
            FileNameText.Text = "Нет фотографий для просмотра";
            return;
        }

        var item = _items[_index];
        _bitmap = LoadOriented(item.FullPath);
        PhotoImage.Source = _bitmap;
        FileNameText.Text = item.FileName;
        DateText.Text = item.CaptureDateDisplay;
        DetailsText.Text = item.Details;
        CounterText.Text = $"{_index + 1:N0} / {_items.Count:N0}";
        Filmstrip.SelectedIndex = _index;
        Filmstrip.ScrollIntoView(item);
        _fitMode = true;
        // The viewport can still be changing while a maximized window is being laid out.
        // Re-fit after layout so the whole photo is visible, not just a clipped fragment.
        Dispatcher.BeginInvoke(new Action(ApplyFitZoom), DispatcherPriority.Loaded);
    }

    private BitmapSource? LoadOriented(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            var orientation = _metadata.Read(path).Orientation;
            if (orientation is not (3 or 6 or 8)) return bitmap;
            var angle = orientation == 3 ? 180 : orientation == 6 ? 90 : 270;
            var rotated = new TransformedBitmap(bitmap, new RotateTransform(angle));
            rotated.Freeze();
            return rotated;
        }
        catch (Exception ex)
        {
            LoggingService.Error("Viewer image load failed: " + path, ex);
            return null;
        }
    }

    private void ApplyFitZoom()
    {
        if (_bitmap is null || ImageScroll.ViewportWidth <= 10 || ImageScroll.ViewportHeight <= 10) return;
        var sx = Math.Max(0.01, (ImageScroll.ViewportWidth - 24) / _bitmap.PixelWidth);
        var sy = Math.Max(0.01, (ImageScroll.ViewportHeight - 24) / _bitmap.PixelHeight);
        SetZoom(Math.Min(1.0, Math.Min(sx, sy)), keepCenter: false);
    }

    private void SetZoom(double zoom, bool keepCenter = true)
    {
        if (_bitmap is null) return;
        zoom = Math.Clamp(zoom, 0.05, 8.0);
        var oldExtentW = PhotoImage.Width;
        var oldExtentH = PhotoImage.Height;
        var centerX = ImageScroll.HorizontalOffset + ImageScroll.ViewportWidth / 2;
        var centerY = ImageScroll.VerticalOffset + ImageScroll.ViewportHeight / 2;
        var relX = oldExtentW > 0 ? centerX / oldExtentW : 0.5;
        var relY = oldExtentH > 0 ? centerY / oldExtentH : 0.5;

        _zoom = zoom;
        PhotoImage.Width = Math.Max(1, _bitmap.PixelWidth * _zoom);
        PhotoImage.Height = Math.Max(1, _bitmap.PixelHeight * _zoom);
        ImageHost.Width = Math.Max(ImageScroll.ViewportWidth, PhotoImage.Width);
        ImageHost.Height = Math.Max(ImageScroll.ViewportHeight, PhotoImage.Height);
        ZoomTextButton.Content = $"{_zoom * 100:0}%";

        if (keepCenter)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ImageScroll.ScrollToHorizontalOffset(Math.Max(0, relX * PhotoImage.Width - ImageScroll.ViewportWidth / 2));
                ImageScroll.ScrollToVerticalOffset(Math.Max(0, relY * PhotoImage.Height - ImageScroll.ViewportHeight / 2));
            }));
        }
        else
        {
            ImageScroll.ScrollToHorizontalOffset(0);
            ImageScroll.ScrollToVerticalOffset(0);
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        _index = (_index + delta + _items.Count) % _items.Count;
        ShowCurrent();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _fitMode = false; SetZoom(_zoom * 1.2); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _fitMode = false; SetZoom(_zoom / 1.2); }
    private void ActualSize_Click(object sender, RoutedEventArgs e) { _fitMode = false; SetZoom(1.0, keepCenter: false); }
    private void Fit_Click(object sender, RoutedEventArgs e) { _fitMode = true; ApplyFitZoom(); }
    private void ToggleInfo_Click(object sender, RoutedEventArgs e) => ToggleInfo();
    private void ToggleFilmstrip_Click(object sender, RoutedEventArgs e) => ToggleFilmstrip();
    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleInfo()
    {
        _infoVisible = !_infoVisible;
        InfoBorder.Visibility = _infoVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_fitMode) Dispatcher.BeginInvoke(new Action(ApplyFitZoom));
    }

    private void ToggleFilmstrip()
    {
        _filmstripVisible = !_filmstripVisible;
        Filmstrip.Visibility = _filmstripVisible ? Visibility.Visible : Visibility.Collapsed;
        FilmstripRow.Height = _filmstripVisible ? new GridLength(118) : new GridLength(0);
        if (_fitMode) Dispatcher.BeginInvoke(new Action(ApplyFitZoom));
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullscreen == WindowState.Minimized ? WindowState.Normal : _stateBeforeFullscreen;
            _fullscreen = false;
        }
        if (_fitMode) Dispatcher.BeginInvoke(new Action(ApplyFitZoom));
    }

    private void Filmstrip_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Filmstrip.SelectedIndex < 0 || Filmstrip.SelectedIndex == _index) return;
        _index = Filmstrip.SelectedIndex;
        ShowCurrent();
    }

    private void Filmstrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_filmstripVisible) return;
        var scroll = FindDescendantScrollViewer(Filmstrip);
        if (scroll is null) return;

        // A vertical wheel over a horizontal, virtualized filmstrip should move the strip
        // by items. Using pixel offsets here would jump too far because CanContentScroll
        // makes ScrollViewer offsets logical (item-based).
        var steps = Math.Max(1, Math.Abs(e.Delta) / 120) * 3;
        for (var i = 0; i < steps; i++)
        {
            if (e.Delta > 0) scroll.LineLeft();
            else scroll.LineRight();
        }
        e.Handled = true;
    }

    private static System.Windows.Controls.ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer viewer) return viewer;
            var nested = FindDescendantScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _fitMode = false;
        SetZoom(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
        e.Handled = true;
    }

    private void ImageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode) Dispatcher.BeginInvoke(new Action(ApplyFitZoom));
    }

    private void ImageScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_zoom <= 0 || (PhotoImage.Width <= ImageScroll.ViewportWidth && PhotoImage.Height <= ImageScroll.ViewportHeight)) return;
        _panning = true;
        _panStart = e.GetPosition(ImageScroll);
        _panHorizontal = ImageScroll.HorizontalOffset;
        _panVertical = ImageScroll.VerticalOffset;
        ImageScroll.CaptureMouse();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void ImageScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(ImageScroll);
        ImageScroll.ScrollToHorizontalOffset(_panHorizontal - (p.X - _panStart.X));
        ImageScroll.ScrollToVerticalOffset(_panVertical - (p.Y - _panStart.Y));
        e.Handled = true;
    }

    private void ImageScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ImageScroll.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Left: Move(-1); e.Handled = true; break;
            case Key.Right: Move(1); e.Handled = true; break;
            case Key.Add:
            case Key.OemPlus: _fitMode = false; SetZoom(_zoom * 1.2); e.Handled = true; break;
            case Key.Subtract:
            case Key.OemMinus: _fitMode = false; SetZoom(_zoom / 1.2); e.Handled = true; break;
            case Key.D1:
            case Key.NumPad1: _fitMode = false; SetZoom(1.0, keepCenter: false); e.Handled = true; break;
            case Key.F: _fitMode = true; ApplyFitZoom(); e.Handled = true; break;
            case Key.I: ToggleInfo(); e.Handled = true; break;
            case Key.T: ToggleFilmstrip(); e.Handled = true; break;
            case Key.F11: ToggleFullscreen(); e.Handled = true; break;
        }
    }
}
