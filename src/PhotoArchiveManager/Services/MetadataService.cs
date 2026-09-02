using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class MetadataService
{
    public MetadataResult Read(string filePath)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            var geo = gps?.GetGeoLocation();

            var dateOriginal = TryDate(subIfd, ExifDirectoryBase.TagDateTimeOriginal);
            var dateDigitized = TryDate(subIfd, ExifDirectoryBase.TagDateTimeDigitized);
            var dateIfd = TryDate(ifd0, ExifDirectoryBase.TagDateTime);

            DateTime? captureDate;
            string source;
            if (dateOriginal.HasValue)
            {
                captureDate = dateOriginal;
                source = "EXIF DateTimeOriginal";
            }
            else if (dateDigitized.HasValue)
            {
                captureDate = dateDigitized;
                source = "EXIF DateTimeDigitized";
            }
            else if (dateIfd.HasValue)
            {
                captureDate = dateIfd;
                source = "EXIF DateTime";
            }
            else
            {
                captureDate = null;
                source = "";
            }

            return new MetadataResult
            {
                CaptureDate = captureDate,
                CaptureDateSource = source,
                CameraMake = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagMake)),
                CameraModel = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagModel)),
                Orientation = TryInt(ifd0, ExifDirectoryBase.TagOrientation) ?? 1,
                GpsLatitude = geo?.Latitude,
                GpsLongitude = geo?.Longitude
            };
        }
        catch (Exception ex)
        {
            return new MetadataResult { Error = "Метаданные: " + ex.Message };
        }
    }

    private static DateTime? TryDate(MetadataExtractor.Directory? directory, int tag)
    {
        if (directory is null || !directory.ContainsTag(tag)) return null;
        try { return directory.GetDateTime(tag); }
        catch { return null; }
    }

    private static int? TryInt(MetadataExtractor.Directory? directory, int tag)
    {
        if (directory is null || !directory.ContainsTag(tag)) return null;
        try { return directory.GetInt32(tag); }
        catch { return null; }
    }

    private static string Clean(string? value) => value?.Trim().Trim('\0') ?? "";
}
