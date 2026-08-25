namespace RouteTimer.Services.Persistence;

public sealed record GarminActivitySource(string ActivityId, string ActivityName);

public enum TrainingUploadAcceptanceOutcome
{
    Accepted,
    AlreadyImported,
    DuplicateHash
}

public sealed record TrainingUploadAcceptance(
    TrainingUploadAcceptanceOutcome Outcome,
    Guid UploadId,
    Guid JobId);

public interface ITrainingUploadRepository
{
    Task<TrainingUploadAcceptance> AcceptAsync(
        StoredUpload upload,
        DateTimeOffset now,
        GarminActivitySource? garminSource,
        CancellationToken cancellationToken);
}
