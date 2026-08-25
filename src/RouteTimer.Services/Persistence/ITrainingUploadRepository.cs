namespace RouteTimer.Services.Persistence;

public sealed record TrainingUploadAcceptance(bool Accepted, Guid? UploadId, Guid? JobId);

public interface ITrainingUploadRepository
{
    Task<TrainingUploadAcceptance> AcceptAsync(
        StoredUpload upload,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
