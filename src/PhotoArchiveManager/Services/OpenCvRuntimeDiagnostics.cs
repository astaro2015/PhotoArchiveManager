using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace PhotoArchiveManager.Services;

public static class OpenCvRuntimeDiagnostics
{
    private const string NativeLibraryName = "OpenCvSharpExtern";
    private const string NativeFileName = "OpenCvSharpExtern.dll";
    private const string EmbeddedNativeResource = "PhotoArchiveManager.Runtime.OpenCvSharpExtern.dll";
    private const string RuntimeVersionFolder = "OpenCvSharp4-4.13.0.20260627-win-x64";

    private static readonly object Sync = new();
    private static bool _resolverPrepared;
    private static bool _probed;
    private static bool _available;
    private static string _version = "";
    private static string _error = "";
    private static string _portableNativePath = "";
    private static IntPtr _nativeHandle;

    public static bool TryProbe(out string version, out string error)
    {
        lock (Sync)
        {
            if (!_probed)
            {
                _probed = true;
                try
                {
                    PreparePortableNativeBinding();

                    _version = Cv2.GetVersionString() ?? "<unknown>";
                    // Force one tiny native allocation as well. This catches cases where the
                    // managed OpenCvSharp assembly loads but its native bridge does not.
                    using var probe = new Mat(1, 1, MatType.CV_8UC1);
                    _available = !probe.Empty();
                    if (!_available)
                    {
                        _error = "OpenCV создал пустую тестовую матрицу.";
                    }
                    else
                    {
                        LoggingService.Info(
                            "OpenCV native runtime OK: " + _version +
                            "; portableRuntime=" + _portableNativePath +
                            "; loadedModule=" + FindLoadedModulePath(NativeFileName));
                    }
                }
                catch (Exception ex)
                {
                    _available = false;
                    _error = BuildFriendlyError(ex);
                    LoggingService.Error("OpenCV native runtime probe failed", ex);
                }
            }

            version = _version;
            error = _error;
            return _available;
        }
    }

    public static void EnsureAvailable()
    {
        if (TryProbe(out _, out var error)) return;
        throw new InvalidOperationException(error);
    }

    public static string GetLoadedNativeModulePath()
        => FindLoadedModulePath(NativeFileName);

    public static string GetPortableNativePath()
    {
        lock (Sync)
        {
            try
            {
                PreparePortableNativeBinding();
                return _portableNativePath;
            }
            catch (Exception ex)
            {
                return "<portable-runtime-error: " + ex.GetType().Name + ": " + ex.Message + ">";
            }
        }
    }

    /// <summary>
    /// Extracts the known-good OpenCvSharp native bridge embedded in PAM itself and installs
    /// a DllImport resolver before the first OpenCvSharp P/Invoke. The normal .NET single-file
    /// native bundle remains enabled as a fallback for SQLite and other native dependencies,
    /// but face analysis no longer depends on OpenCvSharpExtern being found in TEMP.
    /// </summary>
    private static void PreparePortableNativeBinding()
    {
        if (_resolverPrepared) return;

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException($"Photo Archive Manager {AppPaths.AppVersion} поддерживает OpenCV только на Windows x64.");

        var pamAssembly = typeof(OpenCvRuntimeDiagnostics).Assembly;
        long expectedLength;
        string expectedSha256;
        using (var embedded = pamAssembly.GetManifestResourceStream(EmbeddedNativeResource)
               ?? throw new InvalidOperationException(
                   "В EXE отсутствует встроенный нативный модуль OpenCV (" + EmbeddedNativeResource + ")."))
        {
            expectedLength = embedded.Length;
            expectedSha256 = ComputeSha256Hex(embedded);
        }

        var runtimeDirectory = Path.Combine(AppPaths.DataDirectory, "Runtime", RuntimeVersionFolder);
        Directory.CreateDirectory(runtimeDirectory);
        var destination = Path.Combine(runtimeDirectory, NativeFileName);

        // Verify content, not only byte length. A same-size damaged DLL could otherwise survive forever
        // in Data\Runtime and keep face/Quality analysis broken after the original cause was fixed.
        var needExtract = !File.Exists(destination) || new FileInfo(destination).Length != expectedLength;
        if (!needExtract)
        {
            try
            {
                needExtract = !string.Equals(ComputeSha256Hex(destination), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                needExtract = true;
            }
        }

        if (needExtract)
        {
            var temp = destination + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var embedded = pamAssembly.GetManifestResourceStream(EmbeddedNativeResource)
                       ?? throw new InvalidOperationException("Не удалось повторно открыть встроенный OpenCvSharpExtern.dll."))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                    embedded.CopyTo(output, 1024 * 1024);

                if (new FileInfo(temp).Length != expectedLength ||
                    !string.Equals(ComputeSha256Hex(temp), expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Встроенный OpenCvSharpExtern.dll извлечён с ошибкой контрольной суммы.");

                File.Move(temp, destination, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        _portableNativePath = destination;

        // SetDllImportResolver must run before the first OpenCvSharp native call. We invoke this
        // method at application startup and from the final-EXE self-test before Cv2 is touched.
        NativeLibrary.SetDllImportResolver(
            typeof(Cv2).Assembly,
            static (libraryName, _, _) =>
            {
                if (!IsOpenCvExternName(libraryName))
                    return IntPtr.Zero; // use normal runtime probing for unrelated native imports

                lock (Sync)
                {
                    if (_nativeHandle != IntPtr.Zero)
                        return _nativeHandle;
                    if (string.IsNullOrWhiteSpace(_portableNativePath) || !File.Exists(_portableNativePath))
                        return IntPtr.Zero;

                    _nativeHandle = NativeLibrary.Load(_portableNativePath);
                    return _nativeHandle;
                }
            });

        _resolverPrepared = true;
    }

    private static string ComputeSha256Hex(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return ComputeSha256Hex(stream);
    }

    private static bool IsOpenCvExternName(string libraryName)
    {
        if (string.Equals(libraryName, NativeLibraryName, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(libraryName, NativeFileName, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Path.GetFileNameWithoutExtension(libraryName), NativeLibraryName, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFriendlyError(Exception ex)
    {
        var all = FlattenMessages(ex);
        var nativeHint = all.Contains("OpenCvSharpExtern", StringComparison.OrdinalIgnoreCase)
                         || all.Contains("DllNotFound", StringComparison.OrdinalIgnoreCase)
                         || all.Contains("BadImageFormat", StringComparison.OrdinalIgnoreCase)
                         || all.Contains("native", StringComparison.OrdinalIgnoreCase);

        if (nativeHint)
        {
            return "Нативный модуль OpenCV не загрузился из portable-сборки PAM. " +
                   $"PAM {AppPaths.AppVersion} пытается извлечь собственную копию в Data\\Runtime. " +
                   "Проверьте право записи рядом с EXE и пришлите Data\\Logs, если ошибка повторится. " +
                   "Техническая причина: " + ex.Message;
        }

        return "OpenCV недоступен: " + ex.Message;
    }

    private static string FindLoadedModulePath(string fileName)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, fileName, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrWhiteSpace(module.FileName) ? "<loaded>" : module.FileName;
            }
        }
        catch (Exception ex)
        {
            return "<module-list-error: " + ex.GetType().Name + ">";
        }

        return "<not listed>";
    }

    private static string FlattenMessages(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            parts.Add(current.GetType().Name + ": " + current.Message);
        return string.Join(" | ", parts);
    }
}
