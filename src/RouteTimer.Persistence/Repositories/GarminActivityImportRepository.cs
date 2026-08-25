using Microsoft.EntityFrameworkCore;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class GarminActivityImportRepository(RouteTimerDbContext context) : IGarminActivityImportRepository
{
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
