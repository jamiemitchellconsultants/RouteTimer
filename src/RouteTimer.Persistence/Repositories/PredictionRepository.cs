using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

/// <summary>Owns the database transaction that binds retained GPX content, prediction snapshots, and its durable job.</summary>
public sealed class PredictionRepository(RouteTimerDbContext context) : IPredictionRepository
{
    public async Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        StoredUploadEntity? upload;
        if (context.Database.IsRelational())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO stored_uploads ("Id", "Kind", "FileName", "Content", "Sha256", "CreatedAt")
                VALUES ({creation.Upload.Id}, {creation.Upload.Kind}, {creation.Upload.FileName}, {creation.Upload.Content}, {creation.Upload.Sha256}, {creation.Upload.CreatedAt})
                ON CONFLICT ("Kind", "Sha256") DO NOTHING;
                """, cancellationToken);
            upload = await context.Uploads.SingleAsync(entity => entity.Kind == "gpx" && entity.Sha256 == creation.Upload.Sha256, cancellationToken);
        }
        else
        {
            upload = await context.Uploads.SingleOrDefaultAsync(entity => entity.Kind == "gpx" && entity.Sha256 == creation.Upload.Sha256, cancellationToken);
            if (upload is null)
            {
                upload = new StoredUploadEntity
                {
                    Id = creation.Upload.Id,
                    Kind = "gpx",
                    FileName = creation.Upload.FileName,
                    Content = creation.Upload.Content,
                    Sha256 = creation.Upload.Sha256,
                    CreatedAt = creation.Upload.CreatedAt
                };
                context.Uploads.Add(upload);
            }
        }

        var prediction = new PredictionEntity
        {
            Id = Guid.NewGuid(),
            UploadId = upload.Id,
            RiderModelId = creation.Model.Id,
            ModelVersion = creation.Model.Model.AlgorithmVersion,
            RiderWeightKg = creation.Profile.RiderWeightKg,
            BikeWeightKg = creation.Profile.BikeAndEquipmentWeightKg,
            ModelWasCalibrated = creation.Model.WasCalibrated,
            ModelValidationStatus = creation.Model.Validation.Status.ToString(),
            ModelValidationMedianApe = creation.Model.Validation.MedianAbsolutePercentageError,
            ModelValidationP90Ape = creation.Model.Validation.P90AbsolutePercentageError,
            AssumptionSurface = creation.Assumptions.Surface,
            AssumptionWind = creation.Assumptions.Wind,
            AssumptionWeather = creation.Assumptions.Weather,
            AssumptionMovingOnly = creation.Assumptions.MovingOnly,
            State = PredictionState.Queued.ToString(),
            CreatedAt = creation.CreatedAt
        };
        var job = new AnalysisJobEntity
        {
            Id = Guid.NewGuid(),
            Type = "PredictRoute",
            SubjectId = prediction.Id,
            State = "Queued",
            CreatedAt = creation.CreatedAt
        };
        context.Predictions.Add(prediction);
        context.Jobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new QueuedPredictionSubmission(prediction.Id, job.Id, creation.Model.Id);
    }

    public async Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken)
    {
        var prediction = await context.Predictions
            .Include(entity => entity.Upload)
            .SingleOrDefaultAsync(entity => entity.Id == predictionId, cancellationToken);
        if (prediction?.Upload is null) return null;
        return new PredictionForProcessing(prediction.Id,
            new StoredUpload(prediction.Upload.Id, prediction.Upload.FileName, prediction.Upload.Kind, prediction.Upload.Content, prediction.Upload.Sha256, prediction.Upload.CreatedAt),
            prediction.RiderModelId,
            new RiderProfile(prediction.RiderWeightKg, prediction.BikeWeightKg));
    }

    public async Task PublishAsync(Guid predictionId, PredictionPublication publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var prediction = await context.Predictions.Include(entity => entity.Segments).SingleOrDefaultAsync(entity => entity.Id == predictionId, cancellationToken)
            ?? throw new InvalidOperationException("Prediction does not exist.");
        prediction.Segments.Clear();
        foreach (var segment in publication.Segments)
        {
            prediction.Segments.Add(new PredictionSegmentEntity
            {
                PredictionId = prediction.Id,
                Sequence = segment.Sequence,
                Latitude = segment.Latitude,
                Longitude = segment.Longitude,
                ElevationMetres = segment.ElevationMetres,
                CumulativeDistanceMetres = segment.CumulativeDistanceMetres,
                SegmentDistanceMetres = segment.SegmentDistanceMetres,
                Gradient = segment.Gradient,
                CurvaturePerMetre = segment.CurvaturePerMetre,
                PredictedPowerWatts = segment.PredictedPowerWatts,
                PredictedSpeedMetresPerSecond = segment.PredictedSpeedMetresPerSecond,
                SegmentMovingSeconds = segment.SegmentMovingTime.TotalSeconds,
                CumulativeMovingSeconds = segment.CumulativeMovingTime.TotalSeconds,
                Confidence = segment.Confidence.ToString()
            });
        }

        prediction.DistanceMetres = publication.DistanceMetres;
        prediction.AscentMetres = publication.AscentMetres;
        prediction.MovingSeconds = publication.MovingTime.TotalSeconds;
        prediction.AverageSpeedMetresPerSecond = publication.AverageSpeedMetresPerSecond;
        prediction.AveragePowerWatts = publication.AveragePowerWatts;
        prediction.Confidence = publication.Confidence.ToString();
        prediction.Warnings = publication.Warnings.ToList();
        prediction.State = PredictionState.Succeeded.ToString();
        prediction.CompletedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken)
    {
        var prediction = await context.Predictions.SingleOrDefaultAsync(entity => entity.Id == predictionId, cancellationToken);
        if (prediction is null) return;
        prediction.State = PredictionState.Failed.ToString();
        prediction.Warnings = [$"{code}: {message}"];
        prediction.CompletedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) =>
        (await context.Predictions.AsNoTracking().OrderByDescending(entity => entity.CreatedAt).ToListAsync(cancellationToken))
            .Select(entity => ToSummary(entity, [])).ToList();

    public async Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken)
    {
        var entity = await context.Predictions.AsNoTracking().Include(prediction => prediction.Segments)
            .SingleOrDefaultAsync(prediction => prediction.Id == predictionId, cancellationToken);
        return entity is null ? null : ToDetail(entity, entity.Segments.OrderBy(segment => segment.Sequence).Select(ToSegment).ToList());
    }

    private static PredictionSummary ToSummary(PredictionEntity entity, IReadOnlyList<PersistedPredictionSegment> segments) => new(
        entity.Id, Enum.Parse<PredictionState>(entity.State), entity.DistanceMetres, entity.AscentMetres, ToTime(entity.MovingSeconds),
        entity.AverageSpeedMetresPerSecond, entity.AveragePowerWatts, ToConfidence(entity.Confidence), entity.Warnings,
        entity.RiderModelId, entity.ModelVersion, entity.ModelWasCalibrated, Validation(entity), Profile(entity), Assumptions(entity), entity.CreatedAt, entity.CompletedAt, segments);

    private static PredictionDetail ToDetail(PredictionEntity entity, IReadOnlyList<PersistedPredictionSegment> segments) => new(
        entity.Id, Enum.Parse<PredictionState>(entity.State), entity.DistanceMetres, entity.AscentMetres, ToTime(entity.MovingSeconds),
        entity.AverageSpeedMetresPerSecond, entity.AveragePowerWatts, ToConfidence(entity.Confidence), entity.Warnings,
        entity.RiderModelId, entity.ModelVersion, entity.ModelWasCalibrated, Validation(entity), Profile(entity), Assumptions(entity), entity.CreatedAt, entity.CompletedAt, segments);

    private static ModelValidationSummary Validation(PredictionEntity entity) => new(
        Enum.Parse<ModelValidationStatus>(entity.ModelValidationStatus), entity.ModelValidationMedianApe, entity.ModelValidationP90Ape);
    private static RiderProfile Profile(PredictionEntity entity) => new(entity.RiderWeightKg, entity.BikeWeightKg);
    private static PredictionAssumptions Assumptions(PredictionEntity entity) => new(entity.AssumptionSurface, entity.AssumptionWind, entity.AssumptionWeather, entity.AssumptionMovingOnly);
    private static TimeSpan? ToTime(double? seconds) => seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);
    private static ConfidenceLevel? ToConfidence(string? confidence) => confidence is null ? null : Enum.Parse<ConfidenceLevel>(confidence);
    private static PersistedPredictionSegment ToSegment(PredictionSegmentEntity entity) => new(entity.Sequence, entity.Latitude, entity.Longitude, entity.ElevationMetres,
        entity.CumulativeDistanceMetres, entity.SegmentDistanceMetres, entity.Gradient, entity.CurvaturePerMetre, entity.PredictedPowerWatts,
        entity.PredictedSpeedMetresPerSecond, TimeSpan.FromSeconds(entity.SegmentMovingSeconds), TimeSpan.FromSeconds(entity.CumulativeMovingSeconds), Enum.Parse<ConfidenceLevel>(entity.Confidence));
}
