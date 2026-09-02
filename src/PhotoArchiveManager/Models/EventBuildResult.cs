namespace PhotoArchiveManager.Models;

public sealed record EventBuildResult(
    int EventsCreated,
    int PhotosAssigned,
    int PhotosWithoutReliableDate,
    int PhotosPreservedInManualEvents);
