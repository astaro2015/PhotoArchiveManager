using System.Reflection;
using System.Security.Cryptography;

namespace PhotoArchiveManager.Services;

public sealed class FaceModelService
{
    private readonly string _modelDirectory;
    private readonly object _sync = new();
    private bool _yuNetReady;
    private bool _recognitionReady;

    public FaceModelService(string modelDirectory)
    {
        _modelDirectory = modelDirectory;
    }

    public string SFaceModelPath => Path.Combine(_modelDirectory, "face_recognition_sface_2021dec.onnx");
    public string YuNetModelPath => Path.Combine(_modelDirectory, "face_detection_yunet_2023mar.onnx");

    public void EnsureYuNetReady()
    {
        if (_yuNetReady && File.Exists(YuNetModelPath)) return;

        lock (_sync)
        {
            if (_yuNetReady && File.Exists(YuNetModelPath)) return;
            Directory.CreateDirectory(_modelDirectory);
            ExtractEmbeddedResourceExact("face_detection_yunet_2023mar.onnx", YuNetModelPath, 150_000);
            _yuNetReady = true;
        }
    }

    public void EnsureRecognitionReady()
    {
        EnsureYuNetReady();
        if (_recognitionReady && File.Exists(SFaceModelPath)) return;

        lock (_sync)
        {
            if (_recognitionReady && File.Exists(SFaceModelPath)) return;
            Directory.CreateDirectory(_modelDirectory);
            ExtractEmbeddedResourceExact("face_recognition_sface_2021dec.onnx", SFaceModelPath, 10_000_000);
            _recognitionReady = true;
        }
    }

    /// <summary>
    /// Makes the on-disk model an exact copy of the model embedded in this EXE.
    /// A mere size threshold is not enough: a stale/corrupt model left in Data\Models could
    /// otherwise make face quality or recognition behave differently on another computer.
    /// </summary>
    private static void ExtractEmbeddedResourceExact(string fileName, string destination, long minimumBytes)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException("В EXE отсутствует локальная модель OpenCV: " + fileName);

        long expectedLength;
        byte[] expectedHash;
        using (var resource = assembly.GetManifestResourceStream(resourceName)
               ?? throw new InvalidOperationException("Не удалось открыть встроенную модель: " + resourceName))
        {
            expectedLength = resource.Length;
            if (expectedLength <= minimumBytes)
                throw new InvalidDataException("Встроенная модель повреждена: " + fileName);
            expectedHash = SHA256.HashData(resource);
        }

        if (File.Exists(destination))
        {
            var info = new FileInfo(destination);
            if (info.Length == expectedLength)
            {
                using var current = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                var currentHash = SHA256.HashData(current);
                if (CryptographicOperations.FixedTimeEquals(currentHash, expectedHash))
                    return;
            }

            LoggingService.Info("Replacing stale/corrupt local face model: " + destination);
        }

        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = assembly.GetManifestResourceStream(resourceName)
                   ?? throw new InvalidOperationException("Не удалось повторно открыть встроенную модель: " + resourceName))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }

            var tempInfo = new FileInfo(temp);
            if (tempInfo.Length != expectedLength)
                throw new InvalidDataException("Извлечённая модель имеет неверный размер: " + fileName);

            using (var copied = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var copiedHash = SHA256.HashData(copied);
                if (!CryptographicOperations.FixedTimeEquals(copiedHash, expectedHash))
                    throw new InvalidDataException("Контрольная сумма извлечённой модели не совпала: " + fileName);
            }

            File.Move(temp, destination, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
