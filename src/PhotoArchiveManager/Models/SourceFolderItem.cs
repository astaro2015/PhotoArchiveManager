namespace PhotoArchiveManager.Models;

public sealed class SourceFolderItem
{
    public long Id { get; init; }
    public string Path { get; init; } = "";
    public string DisplayName
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }
}
