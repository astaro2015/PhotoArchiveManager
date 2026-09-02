namespace PhotoArchiveManager.Models;

public sealed record DuplicateAnalysisStatistics(long Groups, long DuplicateFiles, long ExtraCopies, long WastedBytes);
