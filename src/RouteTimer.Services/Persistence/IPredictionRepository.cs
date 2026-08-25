using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Persistence;

public enum PredictionState { Queued, Succeeded, Failed, Cancelled }

public sealed record PredictionAssumptions(string Surface, string Wind, string Weather, bool MovingOnly)
{
    public static PredictionAssumptions RoadCalmDryMovingOnly { get; } = new("road", "calm", "dry", true);
}

public sealed record PredictionUpload(string FileName, Stream Content);

public sealed record QueuedPredictionCreation(
    StoredUpload Upload,
    RiderModelSnapshot Model,
    RiderProfile Profile,
    PredictionAssumptions Assumptions,
    DateTimeOffset CreatedAt);

public sealed record QueuedPredictionSubmission(Guid PredictionId, Guid JobId, Guid ModelId);

public sealed record PredictionForProcessing(Guid Id, StoredUpload Upload, Guid ModelId, RiderProfile Profile);

public sealed record PersistedPredictionSegment(
    int Sequence,
    double Latitude,
    double Longitude,
    double ElevationMetres,
    double CumulativeDistanceMetres,
    double SegmentDistanceMetres,
    double Gradient,
    double CurvaturePerMetre,
    double PredictedPowerWatts,
    double PredictedSpeedMetresPerSecond,
    TimeSpan SegmentMovingTime,
    TimeSpan CumulativeMovingTime,
    ConfidenceLevel Confidence);

public sealed record PredictionPublication(
    double DistanceMetres,
    double AscentMetres,
    TimeSpan MovingTime,
    double AverageSpeedMetresPerSecond,
    double AveragePowerWatts,
    ConfidenceLevel Confidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PersistedPredictionSegment> Segments);

public sealed record PredictionSummary(
    Guid Id,
    PredictionState State,
    double? DistanceMetres,
    double? AscentMetres,
    TimeSpan? MovingTime,
    double? AverageSpeedMetresPerSecond,
    double? AveragePowerWatts,
    ConfidenceLevel? Confidence,
    IReadOnlyList<string> Warnings,
    Guid ModelId,
    string ModelVersion,
    bool ModelWasCalibrated,
    ModelValidationSummary Validation,
    RiderProfile Profile,
    PredictionAssumptions Assumptions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<PersistedPredictionSegment> Segments);

public sealed record PredictionDetail(
    Guid Id,
    PredictionState State,
    double? DistanceMetres,
    double? AscentMetres,
    TimeSpan? MovingTime,
    double? AverageSpeedMetresPerSecond,
    double? AveragePowerWatts,
    ConfidenceLevel? Confidence,
    IReadOnlyList<string> Warnings,
    Guid ModelId,
    string ModelVersion,
    bool ModelWasCalibrated,
    ModelValidationSummary Validation,
    RiderProfile Profile,
    PredictionAssumptions Assumptions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<PersistedPredictionSegment> Segments);

public interface IPredictionRepository
{
    Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken);
    Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken);
    Task<bool> TryPublishAsync(Guid predictionId, Guid jobId, string workerId, PredictionPublication publication, CancellationToken cancellationToken);
    Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken);
    Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken);
    Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken);
}
