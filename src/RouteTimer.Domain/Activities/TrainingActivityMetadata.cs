namespace RouteTimer.Domain.Activities;

public sealed record TrainingActivityMetadata(
    string SourceFileName,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? DeviceManufacturer,
    string? DeviceProduct,
    double? DistanceMetres,
    double? AscentMetres);
