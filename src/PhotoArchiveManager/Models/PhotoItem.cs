using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Services;

namespace PhotoArchiveManager.Models;

public sealed class PhotoItem : ObservableObject
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public long FileSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? CaptureDate { get; init; }
    public string CaptureDateSource { get; init; } = "";
    public string CameraMake { get; init; } = "";
    public string CameraModel { get; init; } = "";
    public string Error { get; init; } = "";

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteDisplay));
                OnPropertyChanged(nameof(FavoriteButtonText));
            }
        }
    }

    private int _rating;
    public int Rating
    {
        get => _rating;
        set
        {
            var normalized = Math.Clamp(value, 0, 5);
            if (SetProperty(ref _rating, normalized))
            {
                OnPropertyChanged(nameof(RatingDisplay));
                OnPropertyChanged(nameof(RatingStars));
            }
        }
    }

    public bool IsManualCaptureDate => CaptureDatePolicy.IsManual(CaptureDateSource);
    public string FavoriteDisplay => IsFavorite ? "★" : "";
    public string FavoriteButtonText => IsFavorite ? "★ В избранном" : "☆ В избранное";
    public string RatingDisplay => Rating > 0 ? $"Рейтинг: {Rating}/5" : "Рейтинг: —";
    public string RatingStars => Rating > 0 ? new string('★', Rating) + new string('☆', 5 - Rating) : "☆☆☆☆☆";

    public string CameraDisplay
    {
        get
        {
            var value = string.Join(" ", new[] { CameraMake, CameraModel }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            return string.IsNullOrWhiteSpace(value) ? "Камера: —" : value;
        }
    }

    public string CaptureDateDisplay =>
        DateTime.TryParse(CaptureDate, out var value) ? value.ToString("dd.MM.yyyy HH:mm:ss") : "Дата: неизвестна";

    public string DimensionsDisplay => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "Размер: —";

    public string FileSizeDisplay
    {
        get
        {
            double size = FileSize;
            string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.##} {units[unit]}";
        }
    }
}
