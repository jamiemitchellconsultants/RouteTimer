using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class GarminActivityImportRepository(RouteTimerDbContext context) : IGarminActivityImportRepository
{
    public async Task<GarminActivityImportLink?> GetAsync(
        string activityId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);

        var import = await context.GarminActivityImports
            .AsNoTracking()
            .Where(candidate => candidate.GarminActivityId == activityId)
            .Select(candidate => new
            {
                candidate.GarminActivityId,
                candidate.ActivityName,
                candidate.UploadId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (import is null)
        {
            return null;
        }

        var jobId = await context.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.ParseTraining.ToString() && job.SubjectId == import.UploadId)
            .Select(job => job.Id)
            .SingleAsync(cancellationToken);
        return new GarminActivityImportLink(
            import.GarminActivityId,
            import.ActivityName,
            import.UploadId,
            jobId);
    }

    public async Task<IReadOnlySet<string>> GetLinkedIdsAsync(
        IReadOnlyCollection<string> activityIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activityIds);

        if (activityIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var requestedIds = activityIds.Distinct(StringComparer.Ordinal).ToArray();
        var linkedIds = await context.GarminActivityImports
            .AsNoTracking()
            .Where(import => requestedIds.Contains(import.GarminActivityId))
            .Select(import => import.GarminActivityId)
            .ToListAsync(cancellationToken);
        return new HashSet<string>(linkedIds, StringComparer.Ordinal);
    }
}
