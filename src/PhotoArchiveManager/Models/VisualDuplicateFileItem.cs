using PhotoArchiveManager.Infrastructure;

namespace PhotoArchiveManager.Models;

public sealed class VisualDuplicateFileItem : ObservableObject
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public long FileSize { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Orientation { get; init; } = 1;
    public string? CaptureDate { get; init; }
    public string CameraMake { get; init; } = "";
    public string CameraModel { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string DHash { get; init; } = "";
    public string AHash { get; init; } = "";
    public int DistanceFromRepresentative { get; init; }
    public int AverageHashDistanceFromRepresentative { get; init; }

    public double QualityScore { get; init; } = -1;
    public double TechnicalScore { get; init; } = -1;
    public double SharpnessScore { get; init; } = -1;
    public double BlurScore { get; init; } = -1;
    public double ExposureScore { get; init; } = -1;
    public double ContrastScore { get; init; } = -1;
    public double NoiseScore { get; init; } = -1;
    public double ResolutionScore { get; init; } = -1;
    public double CompressionScore { get; init; } = -1;
    public int FaceCount { get; init; } = -1;
    public int EyeCount { get; init; } = -1;
    public double FaceScore { get; init; } = -1;
    public double EyeScore { get; init; } = -1;
    public double FacePoseScore { get; init; } = -1;
    public double WorstFaceScore { get; init; } = -1;
    public double EyeOpennessScore { get; init; } = -1;
    public int ClosedEyeCount { get; init; } = -1;
    public double BlinkPenalty { get; init; }
    public string QualityNotes { get; init; } = "";
    public bool HasQuality { get; init; }

    private bool _isRecommended;
    public bool IsRecommended
    {
        get => _isRecommended;
        set
        {
            if (SetProperty(ref _isRecommended, value))
                OnPropertyChanged(nameof(RecommendedDisplay));
        }
    }

    private bool _isMarkedForQuarantine;
    public bool IsMarkedForQuarantine
    {
        get => _isMarkedForQuarantine;
        set => SetProperty(ref _isMarkedForQuarantine, value);
    }

    public double SimilarityPercent => 100.0 * (64 - Math.Clamp(DistanceFromRepresentative, 0, 64)) / 64.0;
    public string SimilarityDisplay => $"{SimilarityPercent:0.0}%";
    public string FileSizeDisplay => ByteFormatter.Format(FileSize);
    public string DimensionsDisplay => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "Размер: —";
    public string MegapixelsDisplay => Width > 0 && Height > 0 ? $"{Width * (long)Height / 1_000_000.0:0.0} МП" : "—";
    public int DisplayPixelWidth => ExifOrientationHelper.SwapsDimensions(Orientation) ? Height : Width;
    public int DisplayPixelHeight => ExifOrientationHelper.SwapsDimensions(Orientation) ? Width : Height;
    public string CaptureDateDisplay => StoredDateTime.TryParse(CaptureDate, out var value)
        ? value.ToString("dd.MM.yyyy HH:mm:ss")
        : "Дата: неизвестна";
    public string CameraDisplay
    {
        get
        {
            var value = string.Join(" ", new[] { CameraMake, CameraModel }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            return string.IsNullOrWhiteSpace(value) ? "Камера: —" : value;
        }
    }

    public string QualityDisplay => HasQuality ? $"{QualityScore:0}/100" : "не оценено";
    public string RecommendedDisplay => IsRecommended ? "★ РЕКОМЕНДУЕТСЯ" : "";
    public string FaceDisplay => !HasQuality || FaceCount < 0
        ? "лица: —"
        : FaceCount == 0
            ? "лица не найдены"
            : $"лица: {FaceCount} · глаза: {Math.Max(0, EyeCount)} · лицо {FaceScore:0}/100";
    public bool HasBlinkWarning => HasQuality && ClosedEyeCount > 0;
    public string BlinkWarningDisplay => HasBlinkWarning
        ? $"⚠ Вероятно закрыты глаза: {ClosedEyeCount} · доп. штраф Quality −{BlinkPenalty:0.#}"
        : "";
    public string QualityComponentsDisplay
    {
        get
        {
            if (!HasQuality) return "Оценка качества ещё не выполнена.";
            var baseText = $"техника {TechnicalScore:0} · резкость {SharpnessScore:0} · смаз {BlurScore:0} · экспозиция {ExposureScore:0} · диапазон {ContrastScore:0} · шум {NoiseScore:0} · JPEG {CompressionScore:0} · сохранность {ResolutionScore:0}";
            if (FaceCount > 0)
            {
                var eyeText = EyeScore >= 0 ? $" · детали глаз {EyeScore:0}" : " · детали глаз —";
                var opennessText = EyeOpennessScore >= 0 ? $" · открытость глаз {EyeOpennessScore:0}" : " · открытость глаз —";
                var blinkText = ClosedEyeCount > 0
                    ? $" · вероятно закрыты {ClosedEyeCount} · доп. штраф −{BlinkPenalty:0.#}"
                    : "";
                return baseText + $" · лица {FaceScore:0} · худшее {WorstFaceScore:0} · поза {FacePoseScore:0}" + eyeText + opennessText + blinkText;
            }
            return baseText;
        }
    }
}
