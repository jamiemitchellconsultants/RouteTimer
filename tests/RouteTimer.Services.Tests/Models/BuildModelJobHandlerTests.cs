using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Models;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Models;

public sealed class BuildModelJobHandlerTests
{
    private static readonly RiderProfile SampleProfile = new(75, 10);
    private static readonly PowerModel SampleModel = ModelFixtures.SimpleModel();

    [Fact]
    public async Task Handle_builds_and_saves_a_model_from_the_profile_and_eligible_activities()
    {
        var profiles = new FakeProfileRepository { Profile = SampleProfile };
        var activities = new FakeTrainingActivityRepository { Activities = ThreeEligibleActivities() };
        var builder = new FakePowerModelBuilder { Result = SampleModel };
        var models = new FakeRiderModelRepository();
        var handler = new BuildModelJobHandler(profiles, activities, builder, models);
        var job = MakeJob();

        await handler.HandleAsync(job, CancellationToken.None);

        Assert.NotNull(models.Saved);
        Assert.Same(SampleModel, models.Saved!.Value.Model.PowerModel);
        Assert.Equal(PhysicalCoefficients.Default, models.Saved.Value.Model.Coefficients);
        Assert.Equal(BuildModelJobHandler.AlgorithmVersion, models.Saved.Value.Model.AlgorithmVersion);
        Assert.Equal(SampleProfile, models.Saved.Value.ProfileSnapshot);
        Assert.False(models.Saved.Value.WasCalibrated);
        Assert.Same(SampleProfile, builder.ReceivedProfile);
        Assert.Same(activities.Activities, builder.ReceivedActivities);
    }

    [Fact]
    public async Task Handle_throws_permanent_exception_when_profile_is_missing()
    {
        var profiles = new FakeProfileRepository { Profile = null };
        var handler = new BuildModelJobHandler(profiles, new FakeTrainingActivityRepository(), new FakePowerModelBuilder(), new FakeRiderModelRepository());
        var job = MakeJob();

        var exception = await Assert.ThrowsAsync<ModelBuildException>(() => handler.HandleAsync(job, CancellationToken.None));

        Assert.Equal("profile-missing", exception.Code);
    }

    [Fact]
    public async Task Handle_throws_permanent_exception_when_no_activities_are_eligible()
    {
        var profiles = new FakeProfileRepository { Profile = SampleProfile };
        var activities = new FakeTrainingActivityRepository { Activities = [IneligibleActivity()] };
        var handler = new BuildModelJobHandler(profiles, activities, new FakePowerModelBuilder(), new FakeRiderModelRepository());
        var job = MakeJob();

        var exception = await Assert.ThrowsAsync<ModelBuildException>(() => handler.HandleAsync(job, CancellationToken.None));

        Assert.Equal("no-eligible-activities", exception.Code);
    }

    [Fact]
    public async Task Handle_saves_insufficient_data_status_when_fewer_than_three_activities_are_eligible()
    {
        var profiles = new FakeProfileRepository { Profile = SampleProfile };
        var activities = new FakeTrainingActivityRepository { Activities = [EligibleActivity(), EligibleActivity(), IneligibleActivity()] };
        var builder = new FakePowerModelBuilder { Result = SampleModel };
        var models = new FakeRiderModelRepository();
        var handler = new BuildModelJobHandler(profiles, activities, builder, models);
        var job = MakeJob();

        await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal(ModelValidationStatus.InsufficientData, models.Saved!.Value.Validation.Status);
    }

    [Fact]
    public async Task Handle_saves_not_validated_status_when_three_or_more_activities_are_eligible()
    {
        var profiles = new FakeProfileRepository { Profile = SampleProfile };
        var activities = new FakeTrainingActivityRepository { Activities = ThreeEligibleActivities() };
        var builder = new FakePowerModelBuilder { Result = SampleModel };
        var models = new FakeRiderModelRepository();
        var handler = new BuildModelJobHandler(profiles, activities, builder, models);
        var job = MakeJob();

        await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal(ModelValidationStatus.NotValidated, models.Saved!.Value.Validation.Status);
    }

    private static AnalysisJob MakeJob() =>
        new(Guid.NewGuid(), JobType.BuildModel, ModelSubject.Id, JobState.Running, 1, "worker-1", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

    private static IReadOnlyList<CleanedActivity> ThreeEligibleActivities() => [EligibleActivity(), EligibleActivity(), EligibleActivity()];

    private static CleanedActivity EligibleActivity()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[] { new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51, -2, 100), 7, 200, null, null, false, 0) };
        return new CleanedActivity("Ride", samples, TimeSpan.FromMinutes(20), new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));
    }

    private static CleanedActivity IneligibleActivity()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[] { new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51, -2, 100), 7, null, null, null, false, 0) };
        return new CleanedActivity("Short Ride", samples, TimeSpan.FromMinutes(1), new ActivityQuality(ActivityEligibility.Ineligible, 0.1, 0.1, 0.1, 0, new Dictionary<string, int> { ["gap"] = 1 }, ["too-short"]));
    }

    private sealed class FakeProfileRepository : IProfileRepository
    {
        public RiderProfile? Profile { get; init; }

        public Task<RiderProfile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTrainingActivityRepository : ITrainingActivityRepository
    {
        public IReadOnlyList<CleanedActivity> Activities { get; init; } = [];

        public Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(Activities);
    }

    private sealed class FakePowerModelBuilder : IPowerModelBuilder
    {
        public PowerModel? Result { get; init; }
        public RiderProfile? ReceivedProfile { get; private set; }
        public IReadOnlyList<CleanedActivity>? ReceivedActivities { get; private set; }

        public PowerModel Build(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
        {
            ReceivedProfile = profile;
            ReceivedActivities = activities;
            return Result!;
        }
    }

    private sealed class FakeRiderModelRepository : IRiderModelRepository
    {
        public (RiderModel Model, RiderProfile ProfileSnapshot, bool WasCalibrated, ModelValidationSummary Validation)? Saved { get; private set; }

        public Task<Guid> SaveAsync(RiderModel model, RiderProfile profileSnapshot, bool wasCalibrated, ModelValidationSummary validation, CancellationToken cancellationToken)
        {
            Saved = (model, profileSnapshot, wasCalibrated, validation);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<RiderModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RiderModelSnapshot?> GetAsync(Guid modelId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
