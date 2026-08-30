using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Tests.Adjustments;

/// <summary>
/// A <see cref="PredictionAdjustmentJobHandler"/> wired to in-memory repositories holding one
/// succeeded baseline, one queued adjustment, and one strategy handler, so a test only has to say how
/// that handler misbehaves.
/// </summary>
internal sealed class AdjustmentJobHandlerHarness
{
    public static readonly Guid PredictionId = Guid.NewGuid();
    public static readonly Guid AdjustmentId = Guid.NewGuid();
    public static readonly Guid ModelId = Guid.NewGuid();

    private readonly PredictionAdjustmentJobHandler _jobHandler;

    public AdjustmentJobHandlerHarness(IPacingStrategyHandler handler, string? storedStrategyJson = null)
    {
        Adjustments = new HarnessAdjustmentRepository
        {
            ForProcessing = new AdjustmentForProcessing(
                AdjustmentId, PredictionId, handler.Type, storedStrategyJson ?? "canonical"),
        };
        Predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() };
        Models = new HarnessRiderModelRepository { Model = ModelSnapshot() };
        Progress = new HarnessProgressReporter();
        _jobHandler = new PredictionAdjustmentJobHandler(
            Adjustments, Predictions, Models, new PacingStrategyDispatcher([handler], []), Progress);
    }

    public HarnessAdjustmentRepository Adjustments { get; }
    public HarnessPredictionRepository Predictions { get; }
    public HarnessRiderModelRepository Models { get; }
    public HarnessProgressReporter Progress { get; }

    public Task HandleAsync(CancellationToken cancellationToken = default) =>
        _jobHandler.HandleAsync(Job(), cancellationToken);

    public static AnalysisJob Job() => new(
        AdjustmentId, JobType.AdjustPrediction, AdjustmentId, JobState.Running, 0, "running", 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "worker-a",
        DateTimeOffset.UtcNow.AddMinutes(5));

    public static PredictionDetail SucceededBaseline() => new(
        PredictionId, PredictionState.Succeeded, 100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, [],
        ModelId, "v1", true, new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08), new RiderProfile(75, 10),
        PredictionAssumptions.RoadCalmDryMovingOnly, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        [new PersistedPredictionSegment(1, 51.1, -2.1, 100, 100, 100, .02, 0, 200, 5, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)]);

    public static RiderModelSnapshot ModelSnapshot() => new(
        ModelId, DateTimeOffset.UtcNow, new RiderProfile(75, 10),
        new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, true, "v1"),
        new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08));

    public sealed record TestReport(PacingStrategyType Type) : PacingStrategyReport(Type);
}

internal sealed record HarnessDefinition(PacingStrategyType Type) : PacingStrategyDefinition(Type);

/// <summary>A strategy handler that does exactly what a test tells it to: return a computation, or throw.</summary>
internal sealed class RecordingHandler(PacingStrategyType type) : IPacingStrategyHandler
{
    public PacingStrategyComputation? RunResult { get; set; }
    public Exception? RunException { get; set; }
    public int RunCount { get; private set; }

    public PacingStrategyType Type => type;
    public string Canonicalize(PacingStrategyDefinition strategy) => throw new NotSupportedException();
    public PacingStrategyDefinition Deserialize(string canonicalJson) => new HarnessDefinition(type);
    public string CanonicalizeReport(PacingStrategyReport report) => "report-json";

    public PacingStrategyComputation Run(PacingStrategyContext context, PacingStrategyDefinition strategy, CancellationToken cancellationToken)
    {
        RunCount++;
        return RunException is not null
            ? throw RunException
            : RunResult ?? throw new InvalidOperationException("No result configured.");
    }
}

internal sealed class HarnessAdjustmentRepository : IPredictionAdjustmentRepository
{
    public AdjustmentForProcessing? ForProcessing { get; set; }
    public bool PublishResult { get; set; } = true;
    public List<(Guid AdjustmentId, Guid JobId, string WorkerId, AdjustmentPublication Publication)> PublishCalls { get; } = [];

    public Task<QueuedAdjustmentCreationResult> CreateQueuedAsync(QueuedAdjustmentCreation creation, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<AdjustmentForProcessing?> GetForProcessingAsync(Guid adjustmentId, CancellationToken cancellationToken) => Task.FromResult(ForProcessing);

    public Task<bool> TryPublishAsync(Guid adjustmentId, Guid jobId, string workerId, AdjustmentPublication publication, CancellationToken cancellationToken)
    {
        PublishCalls.Add((adjustmentId, jobId, workerId, publication));
        return Task.FromResult(PublishResult);
    }

    public Task<bool> DeleteAsync(Guid predictionId, Guid adjustmentId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task FailAsync(Guid adjustmentId, string code, string message, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<PredictionAdjustmentSummary>> GetSummariesAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PredictionAdjustmentDetail?> GetAsync(Guid predictionId, Guid adjustmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class HarnessPredictionRepository : IPredictionRepository
{
    public PredictionDetail? Detail { get; set; }

    public Task<QueuedPredictionSubmission> CreateQueuedAsync(QueuedPredictionCreation creation, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PredictionForProcessing?> GetForProcessingAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> TryPublishAsync(Guid predictionId, Guid jobId, string workerId, PredictionPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid predictionId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task FailAsync(Guid predictionId, string code, string message, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<PredictionSummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PredictionDetail?> GetAsync(Guid predictionId, CancellationToken cancellationToken) => Task.FromResult(Detail);
    public Task<RouteTimer.Services.Routes.PredictionGpxSource?> GetGpxSourceAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RecordGarminCourseAsync(Guid predictionId, long courseId, DateTimeOffset uploadedAt, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class HarnessRiderModelRepository : IRiderModelRepository
{
    public RiderModelSnapshot? Model { get; set; }

    public Task<Guid> SaveAsync(RiderModel model, RiderProfile profileSnapshot, ModelValidationSummary validation, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<RiderModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<RiderModelSnapshot?> GetAsync(Guid modelId, CancellationToken cancellationToken) => Task.FromResult(Model);
}

internal sealed class HarnessProgressReporter : IJobProgressReporter
{
    public List<(int Percent, string Stage)> Calls { get; } = [];

    public Task ReportAsync(AnalysisJob job, int progressPercent, string stage, CancellationToken cancellationToken)
    {
        Calls.Add((progressPercent, stage));
        return Task.CompletedTask;
    }
}
