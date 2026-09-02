namespace PhotoArchiveManager.Models;

public sealed record LibraryStatistics(long TotalFiles, long TotalBytes, long ErrorFiles, long MissingFiles);
