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
            SourceFileName = activity.Metadata.SourceFileName,
            StartedAt = activity.Metadata.StartedAt,
            EndedAt = activity.Metadata.EndedAt,
            DeviceManufacturer = activity.Metadata.DeviceManufacturer,
            DeviceProduct = activity.Metadata.DeviceProduct,
            DistanceMetres = activity.Metadata.DistanceMetres,
            AscentMetres = activity.Metadata.AscentMetres,
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

    public async Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken cancellationToken)
    {
        var entities = await context.TrainingActivities
            .AsNoTracking()
            .OrderByDescending(activity => activity.CreatedAt)
            .ToListAsync(cancellationToken);
        return entities.Select(ToSummary).ToList();
    }

    public async Task<TrainingActivityDetail?> GetDetailAsync(Guid activityId, CancellationToken cancellationToken)
    {
        var entity = await context.TrainingActivities
            .AsNoTracking()
            .SingleOrDefaultAsync(activity => activity.Id == activityId, cancellationToken);
        return entity is null
            ? null
            : new TrainingActivityDetail(ToSummary(entity), entity.ExclusionCounts);
    }

    public async Task<TrainingActivityCounts> GetCountsAsync(CancellationToken cancellationToken)
    {
        var eligible = ActivityEligibility.Eligible.ToString();
        var total = await context.TrainingActivities.CountAsync(cancellationToken);
        var eligibleCount = await context.TrainingActivities.CountAsync(activity => activity.Eligibility == eligible, cancellationToken);
        return new TrainingActivityCounts(total, eligibleCount);
    }

    public async Task<bool> DeleteAsync(Guid activityId, CancellationToken cancellationToken)
    {
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var entity = await context.TrainingActivities
            .Include(activity => activity.Samples)
            .SingleOrDefaultAsync(activity => activity.Id == activityId, cancellationToken);
        if (entity is null)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return false;
        }

        var upload = await context.Uploads.SingleOrDefaultAsync(value => value.Id == entity.UploadId, cancellationToken);
        context.ActivitySamples.RemoveRange(entity.Samples);
        context.TrainingActivities.Remove(entity);
        if (upload is not null)
        {
            context.Uploads.Remove(upload);
        }

        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
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

        var startedAt = entity.StartedAt ?? samples.FirstOrDefault()?.Timestamp ?? entity.CreatedAt;
        var endedAt = entity.EndedAt ?? samples.LastOrDefault()?.Timestamp ?? entity.CreatedAt;
        var metadata = new TrainingActivityMetadata(
            string.IsNullOrWhiteSpace(entity.SourceFileName) ? entity.Name : entity.SourceFileName,
            startedAt,
            endedAt,
            entity.DeviceManufacturer,
            entity.DeviceProduct,
            entity.DistanceMetres,
            entity.AscentMetres);

        return new CleanedActivity(entity.Name, samples, TimeSpan.FromSeconds(entity.MovingDurationSeconds), quality, metadata);
    }

    private static TrainingActivitySummary ToSummary(TrainingActivityEntity entity)
    {
        var startedAt = entity.StartedAt ?? entity.CreatedAt;
        var endedAt = entity.EndedAt ?? entity.CreatedAt;
        var metadata = new TrainingActivityMetadata(
            string.IsNullOrWhiteSpace(entity.SourceFileName) ? entity.Name : entity.SourceFileName,
            startedAt,
            endedAt,
            entity.DeviceManufacturer,
            entity.DeviceProduct,
            entity.DistanceMetres,
            entity.AscentMetres);

        return new TrainingActivitySummary(
            entity.Id,
            entity.UploadId,
            metadata,
            TimeSpan.FromSeconds(entity.MovingDurationSeconds),
            Enum.Parse<ActivityEligibility>(entity.Eligibility),
            entity.PositionCoverage,
            entity.ElevationCoverage,
            entity.SpeedCoverage,
            entity.PowerCoverage,
            entity.ReasonCodes,
            entity.CreatedAt);
    }
}
