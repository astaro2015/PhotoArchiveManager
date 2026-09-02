using System.Reflection;

namespace PhotoArchiveManager.Services;

public sealed class FaceModelService
{
    private readonly string _modelDirectory;
    private readonly object _sync = new();
    private bool _detectionReady;
    private bool _recognitionReady;

    public FaceModelService(string modelDirectory)
    {
        _modelDirectory = modelDirectory;
    }

    public string FaceCascadePath => Path.Combine(_modelDirectory, "haarcascade_frontalface_default.xml");
    public string EyeCascadePath => Path.Combine(_modelDirectory, "haarcascade_eye_tree_eyeglasses.xml");
    public string SFaceModelPath => Path.Combine(_modelDirectory, "face_recognition_sface_2021dec.onnx");
    public string YuNetModelPath => Path.Combine(_modelDirectory, "face_detection_yunet_2023mar.onnx");

    // Backward-compatible name used by the quality analyzer.
    public void EnsureReady() => EnsureDetectionReady();

    public void EnsureDetectionReady()
    {
        if (_detectionReady && File.Exists(FaceCascadePath) && File.Exists(EyeCascadePath)) return;

        lock (_sync)
        {
            if (_detectionReady && File.Exists(FaceCascadePath) && File.Exists(EyeCascadePath)) return;
            Directory.CreateDirectory(_modelDirectory);
            ExtractEmbeddedResource("haarcascade_frontalface_default.xml", FaceCascadePath, 1000);
            ExtractEmbeddedResource("haarcascade_eye_tree_eyeglasses.xml", EyeCascadePath, 1000);
            _detectionReady = true;
        }
    }

    public void EnsureRecognitionReady()
    {
        EnsureDetectionReady();
        if (_recognitionReady && RecognitionModelsPresent()) return;

        lock (_sync)
        {
            if (_recognitionReady && RecognitionModelsPresent()) return;
            Directory.CreateDirectory(_modelDirectory);
            ExtractEmbeddedResource("face_recognition_sface_2021dec.onnx", SFaceModelPath, 10_000_000);
            ExtractEmbeddedResource("face_detection_yunet_2023mar.onnx", YuNetModelPath, 150_000);
            _recognitionReady = true;
        }
    }

    private bool RecognitionModelsPresent()
        => File.Exists(SFaceModelPath) && new FileInfo(SFaceModelPath).Length > 10_000_000
           && File.Exists(YuNetModelPath) && new FileInfo(YuNetModelPath).Length > 150_000;

    private static void ExtractEmbeddedResource(string fileName, string destination, long minimumBytes)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > minimumBytes) return;

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException("В EXE отсутствует локальная модель OpenCV: " + fileName);

        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var input = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Не удалось открыть встроенную модель: " + resourceName);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);

            if (new FileInfo(temp).Length <= minimumBytes)
                throw new InvalidDataException("Встроенная модель повреждена: " + fileName);

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temp, destination);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
