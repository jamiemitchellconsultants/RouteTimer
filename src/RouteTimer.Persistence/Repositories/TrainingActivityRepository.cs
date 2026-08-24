using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Routes;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class TrainingActivityRepository(RouteTimerDbContext context) : ITrainingActivityRepository
{
    public async Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var id = Guid.NewGuid();
        var entity = new TrainingActivityEntity
        {
            Id = id,
            UploadId = uploadId,
            Name = activity.Name,
            MovingDurationSeconds = activity.MovingDuration.TotalSeconds,
            Eligibility = activity.Quality.Eligibility.ToString(),
            PositionCoverage = activity.Quality.PositionCoverage,
            ElevationCoverage = activity.Quality.ElevationCoverage,
            SpeedCoverage = activity.Quality.SpeedCoverage,
            PowerCoverage = activity.Quality.PowerCoverage,
            ExclusionCounts = activity.Quality.ExclusionCounts,
            ReasonCodes = activity.Quality.ReasonCodes,
            CreatedAt = DateTimeOffset.UtcNow
        };

        for (var sequence = 0; sequence < activity.Samples.Count; sequence++)
        {
            var sample = activity.Samples[sequence];
            entity.Samples.Add(new ActivitySampleEntity
            {
                ActivityId = id,
                Sequence = sequence,
                Timestamp = sample.Timestamp,
                MovingElapsedSeconds = sample.MovingElapsed.TotalSeconds,
                Latitude = sample.Position.Latitude,
                Longitude = sample.Position.Longitude,
                ElevationMetres = sample.Position.ElevationMetres,
                SpeedMetresPerSecond = sample.SpeedMetresPerSecond,
                PowerWatts = sample.PowerWatts,
                HeartRate = sample.HeartRate,
                Cadence = sample.Cadence,
                CrossesDiscontinuity = sample.CrossesDiscontinuity,
                Gradient = sample.Gradient,
                CurvaturePerMetre = sample.CurvaturePerMetre
            });
        }

        context.TrainingActivities.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken)
    {
        var entity = await context.TrainingActivities
            .Include(activity => activity.Samples)
            .SingleOrDefaultAsync(activity => activity.Id == activityId, cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken)
    {
        var entities = await context.TrainingActivities
            .Include(activity => activity.Samples)
            .ToListAsync(cancellationToken);
        return entities.Select(ToDomain).ToList();
    }

    private static CleanedActivity ToDomain(TrainingActivityEntity entity)
    {
        var samples = entity.Samples
            .OrderBy(sample => sample.Sequence)
            .Select(sample => new CleanRideSample(
                sample.Timestamp,
                TimeSpan.FromSeconds(sample.MovingElapsedSeconds),
                new GeoPoint(sample.Latitude, sample.Longitude, sample.ElevationMetres),
                sample.SpeedMetresPerSecond,
                sample.PowerWatts,
                sample.HeartRate,
                sample.Cadence,
                sample.CrossesDiscontinuity,
                sample.Gradient,
                sample.CurvaturePerMetre))
            .ToList();

        var quality = new ActivityQuality(
            Enum.Parse<ActivityEligibility>(entity.Eligibility),
            entity.PositionCoverage,
            entity.ElevationCoverage,
            entity.SpeedCoverage,
            entity.PowerCoverage,
            entity.ExclusionCounts,
            entity.ReasonCodes);

        return new CleanedActivity(entity.Name, samples, TimeSpan.FromSeconds(entity.MovingDurationSeconds), quality);
    }
}
