namespace PhotoArchiveManager.Models;

public static class ByteFormatter
{
    public static string Format(long bytes)
    {
        double value = Math.Max(0, bytes);
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ"];
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:0.##} {units[i]}";
    }
}
