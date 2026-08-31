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

public sealed class PacingStrategyDispatcherTests
{
    // Break caught: two handlers silently claiming the same strategy type let one shadow the other at random.
    [Fact]
    public void Construction_fails_on_duplicate_handlers_for_the_same_type()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new PacingStrategyDispatcher(
            [new FakeHandler(PacingStrategyType.TimeTarget), new FakeHandler(PacingStrategyType.TimeTarget)],
            []));

        Assert.Contains("TimeTarget", exception.Message);
    }

    // Break caught: an enabled strategy with no registered handler is only discovered when the first request for it fails.
    [Fact]
    public void Construction_fails_when_an_enabled_type_has_no_registered_handler()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new PacingStrategyDispatcher(
            [], [PacingStrategyType.NpIfTarget]));

        Assert.Contains("NpIfTarget", exception.Message);
    }

    [Fact]
    public void Construction_succeeds_when_every_enabled_type_has_exactly_one_handler()
    {
        var dispatcher = new PacingStrategyDispatcher(
            [new FakeHandler(PacingStrategyType.TimeTarget), new FakeHandler(PacingStrategyType.NpIfTarget)],
            [PacingStrategyType.TimeTarget]);

        Assert.True(dispatcher.IsEnabled(PacingStrategyType.TimeTarget));
        Assert.False(dispatcher.IsEnabled(PacingStrategyType.NpIfTarget));
    }

    // Break caught: creation-time lookup ignores the enabled flag, letting a disabled strategy still be requested.
    [Fact]
    public void TryGetHandlerForCreation_returns_null_for_a_registered_but_disabled_type()
    {
        var dispatcher = new PacingStrategyDispatcher([new FakeHandler(PacingStrategyType.TimeTarget)], []);

        Assert.Null(dispatcher.TryGetHandlerForCreation(PacingStrategyType.TimeTarget));
    }

    // Break caught: disabling a strategy after creation strands its already-queued jobs with no handler to process them.
    [Fact]
    public void GetHandlerForProcessing_ignores_the_enabled_flag()
    {
        var dispatcher = new PacingStrategyDispatcher([new FakeHandler(PacingStrategyType.TimeTarget)], []);

        Assert.NotNull(dispatcher.GetHandlerForProcessing(PacingStrategyType.TimeTarget));
    }

    private sealed class FakeHandler(PacingStrategyType type) : IPacingStrategyHandler
    {
        public PacingStrategyType Type => type;
        public string Canonicalize(PacingStrategyDefinition strategy) => throw new NotSupportedException();
        public PacingStrategyDefinition Deserialize(string canonicalJson) => throw new NotSupportedException();
        public string CanonicalizeReport(PacingStrategyReport report) => throw new NotSupportedException();
        public PacingStrategyComputation Run(PacingStrategyContext context, PacingStrategyDefinition strategy, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed class PredictionAdjustmentServiceTests
{
    // Break caught: creating an adjustment for a disabled strategy still inserts a row or enqueues a job.
    [Fact]
    public async Task CreateAsync_rejects_a_disabled_strategy_without_touching_the_repository_or_queue()
    {
        var adjustments = new FakeAdjustmentRepository();
        var jobs = new FakeJobQueue();
        var dispatcher = new PacingStrategyDispatcher([], []);
        var service = new PredictionAdjustmentService(adjustments, dispatcher, jobs, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentException>(
            () => service.CreateAsync(Guid.NewGuid(), new TestDefinition(PacingStrategyType.TimeTarget), CancellationToken.None));

        Assert.Equal("pacing-strategy-disabled", exception.Code);
        Assert.Empty(adjustments.CreateCalls);
        Assert.Empty(jobs.EnqueueCalls);
    }

    [Theory]
    [InlineData(AdjustmentBaselineStatus.BaselineNotFound, "prediction-not-found")]
    [InlineData(AdjustmentBaselineStatus.BaselineNotReady, "adjustment-baseline-not-ready")]
    public async Task CreateAsync_translates_baseline_status_failures_without_enqueueing(AdjustmentBaselineStatus status, string expectedCode)
    {
        var adjustments = new FakeAdjustmentRepository { CreateResult = new QueuedAdjustmentCreationResult(status, null) };
        var jobs = new FakeJobQueue();
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget);
        var dispatcher = new PacingStrategyDispatcher([handler], [PacingStrategyType.TimeTarget]);
        var service = new PredictionAdjustmentService(adjustments, dispatcher, jobs, TimeProvider.System);

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentException>(
            () => service.CreateAsync(Guid.NewGuid(), new TestDefinition(PacingStrategyType.TimeTarget), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Empty(jobs.EnqueueCalls);
    }

    // Break caught: creation as "one operation" silently skips canonicalization, insertion, or enqueueing, or does them out of order.
    [Fact]
    public async Task CreateAsync_canonicalizes_inserts_then_enqueues_with_the_adjustment_id_as_subject()
    {
        var predictionId = Guid.NewGuid();
        var adjustmentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var adjustments = new FakeAdjustmentRepository { CreateResult = new QueuedAdjustmentCreationResult(AdjustmentBaselineStatus.Ready, adjustmentId) };
        var jobs = new FakeJobQueue { EnqueueResult = jobId };
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget);
        var dispatcher = new PacingStrategyDispatcher([handler], [PacingStrategyType.TimeTarget]);
        var service = new PredictionAdjustmentService(adjustments, dispatcher, jobs, TimeProvider.System);

        var result = await service.CreateAsync(predictionId, new TestDefinition(PacingStrategyType.TimeTarget), CancellationToken.None);

        Assert.Equal(adjustmentId, result.AdjustmentId);
        Assert.Equal(jobId, result.JobId);
        var create = Assert.Single(adjustments.CreateCalls);
        Assert.Equal(predictionId, create.PredictionId);
        Assert.Equal("canonical", create.StrategyJson);
        Assert.Equal((JobType.AdjustPrediction, adjustmentId), Assert.Single(jobs.EnqueueCalls));
        Assert.Empty(adjustments.DeleteCalls);
    }

    // Break caught: a failed enqueue leaves an orphaned queued adjustment with no job that will ever process it.
    [Fact]
    public async Task CreateAsync_deletes_the_inserted_row_when_enqueueing_fails()
    {
        var predictionId = Guid.NewGuid();
        var adjustmentId = Guid.NewGuid();
        var adjustments = new FakeAdjustmentRepository { CreateResult = new QueuedAdjustmentCreationResult(AdjustmentBaselineStatus.Ready, adjustmentId) };
        var jobs = new FakeJobQueue { EnqueueException = new InvalidOperationException("queue unavailable") };
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget);
        var dispatcher = new PacingStrategyDispatcher([handler], [PacingStrategyType.TimeTarget]);
        var service = new PredictionAdjustmentService(adjustments, dispatcher, jobs, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(predictionId, new TestDefinition(PacingStrategyType.TimeTarget), CancellationToken.None));

        var deleted = Assert.Single(adjustments.DeleteCalls);
        Assert.Equal((predictionId, adjustmentId), deleted);
    }

    private sealed record TestDefinition(PacingStrategyType Type) : PacingStrategyDefinition(Type);

    private sealed class RecordingHandler(PacingStrategyType type) : IPacingStrategyHandler
    {
        public PacingStrategyType Type => type;
        public string Canonicalize(PacingStrategyDefinition strategy) => "canonical";
        public PacingStrategyDefinition Deserialize(string canonicalJson) => throw new NotSupportedException();
        public string CanonicalizeReport(PacingStrategyReport report) => throw new NotSupportedException();
        public PacingStrategyComputation Run(PacingStrategyContext context, PacingStrategyDefinition strategy, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeAdjustmentRepository : IPredictionAdjustmentRepository
    {
        public QueuedAdjustmentCreationResult CreateResult { get; set; } = new(AdjustmentBaselineStatus.Ready, Guid.NewGuid());
        public List<QueuedAdjustmentCreation> CreateCalls { get; } = [];
        public List<(Guid PredictionId, Guid AdjustmentId)> DeleteCalls { get; } = [];

        public Task<QueuedAdjustmentCreationResult> CreateQueuedAsync(QueuedAdjustmentCreation creation, CancellationToken cancellationToken)
        {
            CreateCalls.Add(creation);
            return Task.FromResult(CreateResult);
        }

        public Task<AdjustmentForProcessing?> GetForProcessingAsync(Guid adjustmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryPublishAsync(Guid adjustmentId, Guid jobId, string workerId, AdjustmentPublication publication, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid predictionId, Guid adjustmentId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            DeleteCalls.Add((predictionId, adjustmentId));
            return Task.FromResult(true);
        }

        public Task FailAsync(Guid adjustmentId, string code, string message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PredictionAdjustmentSummary>> GetSummariesAsync(Guid predictionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PredictionAdjustmentDetail?> GetAsync(Guid predictionId, Guid adjustmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeJobQueue : IJobQueue
    {
        public Guid EnqueueResult { get; set; } = Guid.NewGuid();
        public Exception? EnqueueException { get; set; }
        public List<(JobType Type, Guid SubjectId)> EnqueueCalls { get; } = [];

        public Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
        {
            EnqueueCalls.Add((type, subjectId));
            return EnqueueException is not null ? Task.FromException<Guid>(EnqueueException) : Task.FromResult(EnqueueResult);
        }

        public Task<Guid> EnqueueIfNotPendingAsync(JobType type, Guid subjectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReportProgressAsync(Guid jobId, string workerId, int progressPercent, string stage, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed class PredictionAdjustmentJobHandlerTests
{
    private static readonly Guid PredictionId = Guid.NewGuid();
    private static readonly Guid AdjustmentId = Guid.NewGuid();
    private static readonly Guid ModelId = Guid.NewGuid();

    // Break caught: the handler's stages, percentages, or order silently drift from LoadingBaseline -> PreparingStrategy -> Simulating -> Publishing.
    [Fact]
    public async Task HandleAsync_reports_the_four_stages_in_order_and_publishes_the_computation()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() };
        var models = new HarnessRiderModelRepository { Model = ModelSnapshot() };
        var progress = new HarnessProgressReporter();
        var handlerRun = new PacingStrategyComputation(
            new PredictionResult([new PredictionSegment(1, 100, .02, 200, 5, TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)],
                TimeSpan.FromSeconds(20), ConfidenceLevel.Medium, []),
            new AdjustmentJobHandlerHarness.TestReport(PacingStrategyType.TimeTarget), new Dictionary<int, PredictionAdjustmentAnnotation> { [1] = new(2, "burn", 500) }, [], "time-target-v1");
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget) { RunResult = handlerRun };
        var dispatcher = new PacingStrategyDispatcher([handler], []);
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, models, dispatcher, progress);

        await jobHandler.HandleAsync(Job(), CancellationToken.None);

        Assert.Equal(
        [
            (5, JobProgressStages.LoadingBaseline),
            (25, JobProgressStages.PreparingStrategy),
            (45, JobProgressStages.Simulating),
            (90, JobProgressStages.Publishing),
        ], progress.Calls);
        var published = Assert.Single(adjustments.PublishCalls);
        Assert.Equal(TimeSpan.FromSeconds(20), published.Publication.MovingTime);
        Assert.Equal(1, published.Publication.Segments.Single().Sequence);
        Assert.Equal(2, published.Publication.Segments.Single().ZoneNumber);
        Assert.Equal("burn", published.Publication.Segments.Single().StrategyPhase);
        Assert.Equal("time-target-v1", published.Publication.AlgorithmVersion);
    }

    [Fact]
    public async Task HandleAsync_fails_when_the_adjustment_no_longer_exists()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = null };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, new HarnessPredictionRepository(), new HarnessRiderModelRepository(), new PacingStrategyDispatcher([], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("adjustment-missing", exception.Code);
    }

    [Fact]
    public async Task HandleAsync_fails_when_the_baseline_no_longer_exists()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = null };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, new HarnessRiderModelRepository(), new PacingStrategyDispatcher([], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("baseline-missing", exception.Code);
    }

    [Fact]
    public async Task HandleAsync_fails_when_the_baseline_has_not_succeeded()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() with { State = PredictionState.Queued } };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, new HarnessRiderModelRepository(), new PacingStrategyDispatcher([], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("baseline-not-ready", exception.Code);
    }

    [Fact]
    public async Task HandleAsync_fails_when_the_captured_model_no_longer_exists()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() };
        var models = new HarnessRiderModelRepository { Model = null };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, models, new PacingStrategyDispatcher([], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("model-missing", exception.Code);
    }

    // Break caught: a baseline persisted with a null moving time, confidence, or no segments (data corruption) crashes the handler instead of failing the job cleanly.
    [Fact]
    public async Task HandleAsync_fails_on_malformed_persisted_baseline_segments()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() with { MovingTime = null } };
        var models = new HarnessRiderModelRepository { Model = ModelSnapshot() };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, models, new PacingStrategyDispatcher([], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("invalid-prediction-adjustment-result", exception.Code);
    }

    // Break caught: a strategy handler that cannot find a physically valid solution leaks PredictionCalculationException past the job boundary.
    [Fact]
    public async Task HandleAsync_translates_a_calculation_failure_from_the_handler()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() };
        var models = new HarnessRiderModelRepository { Model = ModelSnapshot() };
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget) { RunException = new PredictionCalculationException("no solution") };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, models, new PacingStrategyDispatcher([handler], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("invalid-prediction-adjustment-result", exception.Code);
    }

    // Break caught: a computation reporting an unknown warning code is published anyway instead of failing the job.
    [Fact]
    public async Task HandleAsync_rejects_an_unknown_warning_code_from_the_computation()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() };
        var models = new HarnessRiderModelRepository { Model = ModelSnapshot() };
        var handlerRun = new PacingStrategyComputation(
            new PredictionResult([new PredictionSegment(1, 100, .02, 200, 5, TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)],
                TimeSpan.FromSeconds(20), ConfidenceLevel.Medium, []),
            new AdjustmentJobHandlerHarness.TestReport(PacingStrategyType.TimeTarget), new Dictionary<int, PredictionAdjustmentAnnotation>(), ["not-a-real-warning"], "time-target-v1");
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget) { RunResult = handlerRun };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, models, new PacingStrategyDispatcher([handler], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("invalid-prediction-adjustment-result", exception.Code);
        Assert.Empty(adjustments.PublishCalls);
    }

    // Break caught: adjusted segments whose sequences don't match the baseline route are published anyway.
    [Fact]
    public async Task HandleAsync_rejects_a_sequence_mismatch_between_adjusted_segments_and_the_baseline_route()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget) };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() };
        var models = new HarnessRiderModelRepository { Model = ModelSnapshot() };
        var handlerRun = new PacingStrategyComputation(
            new PredictionResult([new PredictionSegment(99, 100, .02, 200, 5, TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)],
                TimeSpan.FromSeconds(20), ConfidenceLevel.Medium, []),
            new AdjustmentJobHandlerHarness.TestReport(PacingStrategyType.TimeTarget), new Dictionary<int, PredictionAdjustmentAnnotation>(), [], "time-target-v1");
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget) { RunResult = handlerRun };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, models, new PacingStrategyDispatcher([handler], []), new HarnessProgressReporter());

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(() => jobHandler.HandleAsync(Job(), CancellationToken.None));
        Assert.Equal("prediction-adjustment-sequence-mismatch", exception.Code);
    }

    // Break caught: TryPublishAsync's ownership guard result is ignored, so a stale-lease publish appears to succeed.
    [Fact]
    public async Task HandleAsync_does_not_throw_when_publish_reports_stale_ownership()
    {
        var adjustments = new HarnessAdjustmentRepository { ForProcessing = AdjustmentFor(PacingStrategyType.TimeTarget), PublishResult = false };
        var predictions = new HarnessPredictionRepository { Detail = SucceededBaseline() };
        var models = new HarnessRiderModelRepository { Model = ModelSnapshot() };
        var handlerRun = new PacingStrategyComputation(
            new PredictionResult([new PredictionSegment(1, 100, .02, 200, 5, TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)],
                TimeSpan.FromSeconds(20), ConfidenceLevel.Medium, []),
            new AdjustmentJobHandlerHarness.TestReport(PacingStrategyType.TimeTarget), new Dictionary<int, PredictionAdjustmentAnnotation>(), [], "time-target-v1");
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget) { RunResult = handlerRun };
        var jobHandler = new PredictionAdjustmentJobHandler(adjustments, predictions, models, new PacingStrategyDispatcher([handler], []), new HarnessProgressReporter());

        await jobHandler.HandleAsync(Job(), CancellationToken.None);

        Assert.Single(adjustments.PublishCalls);
    }

    private static AnalysisJob Job() => new(
        AdjustmentId, JobType.AdjustPrediction, AdjustmentId, JobState.Running, 0, "running", 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "worker-a", DateTimeOffset.UtcNow.AddMinutes(5));

    private static AdjustmentForProcessing AdjustmentFor(PacingStrategyType type) => new(AdjustmentId, PredictionId, type, "canonical");

    private static PredictionDetail SucceededBaseline() => new(
        PredictionId, PredictionState.Succeeded, 100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, [],
        ModelId, "v1", true, new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08), new RiderProfile(75, 10),
        PredictionAssumptions.RoadCalmDryMovingOnly, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        [new PersistedPredictionSegment(1, 51.1, -2.1, 100, 100, 100, .02, 0, 200, 5, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)]);

    private static RiderModelSnapshot ModelSnapshot() => new(
        ModelId, DateTimeOffset.UtcNow, new RiderProfile(75, 10),
        new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, true, "v1"),
        new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08));

}
