namespace RouteTimer.Services.Persistence;

public interface IGarminActivityImportRepository
{
    Task<IReadOnlySet<string>> GetLinkedIdsAsync(
        IReadOnlyCollection<string> activityIds,
        CancellationToken cancellationToken);
}
