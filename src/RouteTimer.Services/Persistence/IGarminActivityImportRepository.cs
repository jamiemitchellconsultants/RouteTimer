namespace RouteTimer.Services.Persistence;

public sealed record GarminActivityImportLink(
    string ActivityId,
    string ActivityName,
    Guid UploadId,
    Guid JobId);

public interface IGarminActivityImportRepository
{
    Task<GarminActivityImportLink?> GetAsync(
        string activityId,
        CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetLinkedIdsAsync(
        IReadOnlyCollection<string> activityIds,
        CancellationToken cancellationToken);
}
