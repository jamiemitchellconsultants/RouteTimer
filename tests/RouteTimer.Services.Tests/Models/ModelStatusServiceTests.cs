using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Models;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Models;

public sealed class ModelStatusServiceTests
{
    private static readonly RiderProfile Profile = new(75, 10);

    [Fact]
    public async Task GetAsync_blocks_on_profile_before_other_missing_model_prerequisites()
    {
        var service = CreateService(hasProfile: false, counts: new TrainingActivityCounts(0, 0), latestBuild: BuildJob(JobState.Running));

        var status = await service.GetAsync(CancellationToken.None);

        Assert.False(status.IsReady);
        Assert.Equal("profile-required", status.BlockingReason);
        Assert.Null(status.CurrentModel);
        Assert.NotNull(status.RebuildJob);
    }

    [Fact]
    public async Task GetAsync_blocks_on_eligible_evidence_before_active_build_without_current_model()
    {
        var service = CreateService(counts: new TrainingActivityCounts(3, 0), latestBuild: BuildJob(JobState.Running));

        var status = await service.GetAsync(CancellationToken.None);

        Assert.False(status.IsReady);
        Assert.Equal("no-eligible-activities", status.BlockingReason);
        Assert.Null(status.CurrentModel);
        Assert.NotNull(status.RebuildJob);
    }

    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Running)]
    public async Task GetAsync_blocks_on_active_build_when_prerequisites_exist_but_no_current_model(JobState state)
    {
        var job = BuildJob(state);
        var service = CreateService(latestBuild: job);

        var status = await service.GetAsync(CancellationToken.None);

        Assert.False(status.IsReady);
        Assert.Equal("model-building", status.BlockingReason);
        Assert.Same(job, status.RebuildJob);
    }

    [Fact]
    public async Task GetAsync_blocks_on_latest_failed_build_when_prerequisites_exist_but_no_current_model()
    {
        var job = BuildJob(JobState.Failed, diagnosticCode: "no-power-evidence");
        var service = CreateService(latestBuild: job);

        var status = await service.GetAsync(CancellationToken.None);

        Assert.False(status.IsReady);
        Assert.Equal("model-build-failed", status.BlockingReason);
        Assert.Same(job, status.RebuildJob);
    }

    [Fact]
    public async Task GetAsync_blocks_as_model_not_ready_when_prerequisites_exist_and_no_build_explains_it()
    {
        var service = CreateService();

        var status = await service.GetAsync(CancellationToken.None);

        Assert.False(status.IsReady);
        Assert.Equal("model-not-ready", status.BlockingReason);
        Assert.Null(status.CurrentModel);
        Assert.Null(status.RebuildJob);
    }

    [Fact]
    public async Task GetAsync_returns_ready_with_current_model()
    {
        var model = CurrentModel();
        var service = CreateService(currentModel: model);

        var status = await service.GetAsync(CancellationToken.None);

        Assert.True(status.IsReady);
        Assert.Null(status.BlockingReason);
        Assert.Same(model, status.CurrentModel);
        Assert.Null(status.RebuildJob);
    }

    [Fact]
    public async Task GetAsync_keeps_current_model_ready_while_rebuild_is_running()
    {
        var model = CurrentModel();
        var job = BuildJob(JobState.Running);
        var service = CreateService(hasProfile: false, counts: new TrainingActivityCounts(0, 0), currentModel: model, latestBuild: job);

        var status = await service.GetAsync(CancellationToken.None);

        Assert.True(status.IsReady);
        Assert.NotNull(status.CurrentModel);
        Assert.Equal(JobState.Running, status.RebuildJob!.State);
        Assert.Null(status.BlockingReason);
    }

    [Fact]
    public async Task GetAsync_attaches_latest_failed_rebuild_as_warning_when_current_model_is_usable()
    {
        var model = CurrentModel();
        var job = BuildJob(JobState.Failed, diagnosticCode: "no-power-evidence");
        var service = CreateService(currentModel: model, latestBuild: job);

        var status = await service.GetAsync(CancellationToken.None);

        Assert.True(status.IsReady);
        Assert.Null(status.BlockingReason);
        Assert.Same(model, status.CurrentModel);
        Assert.Same(job, status.RebuildJob);
    }

    [Fact]
    public async Task GetAsync_translates_invalid_persisted_current_model_to_stable_blocking_reason()
    {
        var models = new FakeRiderModelRepository { CurrentException = new InvalidPersistedRiderModelException("raw database detail") };
        var service = CreateService(models: models);

        var status = await service.GetAsync(CancellationToken.None);

        Assert.False(status.IsReady);
        Assert.Equal("invalid-rider-model", status.BlockingReason);
        Assert.Null(status.CurrentModel);
    }

    private static ModelStatusService CreateService(
        bool hasProfile = true,
        TrainingActivityCounts? counts = null,
        RiderModelSnapshot? currentModel = null,
        AnalysisJob? latestBuild = null,
        FakeRiderModelRepository? models = null) =>
        new(
            new FakeProfileRepository { Profile = hasProfile ? Profile : null },
            new FakeTrainingActivityRepository { Counts = counts ?? new TrainingActivityCounts(3, 2) },
            models ?? new FakeRiderModelRepository { Current = currentModel },
            new FakeJobRepository { Latest = latestBuild });

    private static RiderModelSnapshot CurrentModel() => new(
        Guid.NewGuid(),
        new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
        Profile,
        new RiderModel(ModelFixtures.SimpleModel(), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "route-model-v2"),
        new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08));

    private static AnalysisJob BuildJob(JobState state, string? diagnosticCode = null)
    {
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        return new AnalysisJob(
            Guid.NewGuid(),
            JobType.BuildModel,
            ModelSubject.Id,
            state,
            state == JobState.Succeeded ? 100 : 20,
            state == JobState.Failed ? JobProgressStages.Failed : JobProgressStages.BuildingPowerModel,
            1,
            now,
            state is JobState.Queued ? null : now,
            now,
            state is JobState.Queued or JobState.Running ? null : now,
            state is JobState.Running ? "worker-1" : null,
            state is JobState.Running ? now.AddMinutes(5) : null,
            diagnosticCode,
            diagnosticCode is null ? null : "Build failed.");
    }

    private sealed class FakeProfileRepository : IProfileRepository
    {
        public RiderProfile? Profile { get; init; }

        public Task<RiderProfile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTrainingActivityRepository : ITrainingActivityRepository
    {
        public TrainingActivityCounts Counts { get; init; } = new(0, 0);

        public Task<Guid> SaveAsync(Guid uploadId, RouteTimer.Domain.Activities.CleanedActivity activity, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RouteTimer.Domain.Activities.CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RouteTimer.Domain.Activities.CleanedActivity>> GetAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TrainingActivityDetail?> GetDetailAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TrainingActivityCounts> GetCountsAsync(CancellationToken cancellationToken) => Task.FromResult(Counts);

        public Task<bool> DeleteAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRiderModelRepository : IRiderModelRepository
    {
        public RiderModelSnapshot? Current { get; init; }
        public Exception? CurrentException { get; init; }

        public Task<Guid> SaveAsync(RiderModel model, RiderProfile profileSnapshot, ModelValidationSummary validation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RiderModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken) =>
            CurrentException is null ? Task.FromResult(Current) : Task.FromException<RiderModelSnapshot?>(CurrentException);

        public Task<RiderModelSnapshot?> GetAsync(Guid modelId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeJobRepository : IJobRepository
    {
        public AnalysisJob? Latest { get; init; }

        public Task<AnalysisJob?> GetAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AnalysisJob?> GetLatestAsync(JobType type, Guid subjectId, CancellationToken cancellationToken) => Task.FromResult(Latest);
    }
}
