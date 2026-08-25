using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Models;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Physics;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Tests.Activities;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Models;

public sealed class BuildModelJobHandlerTests
{
    private static readonly RiderProfile SampleProfile = new(75, 10);
    private static readonly PowerModel SampleModel = ModelFixtures.SimpleModel();

    [Fact]
    public async Task Handle_enriches_every_loaded_row_and_saves_the_complete_calibrated_model()
    {
        var profiles = new FakeProfileRepository { Profile = SampleProfile };
        var activities = new FakeTrainingActivityRepository { Activities = [EligibleActivity("eligible"), IneligibleActivity()] };
        var geometry = new FakeTrainingGeometryEnricher();
        var builder = new FakePowerModelBuilder { Result = SampleModel };
        var calibration = new PhysicalCalibrationResult(new PhysicalCoefficients(.96, 1.20, .006, .28), true, "physics-calibrated");
        var calibrator = new FakePhysicsCalibrator { Result = calibration };
        var descentModel = LearnedDescentModel();
        var descents = new FakeDescentLimitBuilder { Result = descentModel };
        var validation = new ModelValidationSummary(ModelValidationStatus.Passed, .05, .09);
        var validator = new FakeModelValidator { Result = validation };
        var models = new FakeRiderModelRepository();
        var handler = new BuildModelJobHandler(profiles, activities, geometry, builder, calibrator, descents, validator, models);

        await handler.HandleAsync(MakeJob(), CancellationToken.None);

        Assert.Equal(activities.Activities, geometry.Inputs);
        Assert.Equal(2, geometry.Outputs.Count);
        Assert.Equal(geometry.Outputs, builder.ReceivedActivities);
        Assert.Same(builder.ReceivedActivities, calibrator.ReceivedActivities);
        Assert.Same(builder.ReceivedActivities, descents.ReceivedActivities);
        Assert.Same(builder.ReceivedActivities, validator.ReceivedActivities);
        Assert.Equal(0, activities.SaveCount);
        Assert.Equal(
            new RiderModel(SampleModel, calibration.Coefficients, descentModel, true, "route-model-v2"),
            models.Saved!.Value.Model);
        Assert.Equal(SampleProfile, models.Saved.Value.ProfileSnapshot);
        Assert.Equal(validation, models.Saved.Value.Validation);
    }

    [Fact]
    public async Task Handle_recomputes_legacy_geometry_in_memory_without_persisting_it()
    {
        var profiles = new FakeProfileRepository { Profile = SampleProfile };
        var eligible = LegacyGeometryActivity("eligible", ActivityEligibility.Eligible);
        var ineligible = LegacyGeometryActivity("ineligible", ActivityEligibility.Ineligible);
        var activities = new FakeTrainingActivityRepository { Activities = [eligible, ineligible] };
        var builder = new FakePowerModelBuilder { Result = SampleModel };
        var calibrator = new FakePhysicsCalibrator();
        var descents = new FakeDescentLimitBuilder();
        var validator = new FakeModelValidator();
        var models = new FakeRiderModelRepository();
        var handler = new BuildModelJobHandler(
            profiles,
            activities,
            new TrainingGeometryEnricher(RouteProcessingOptions.Default),
            builder,
            calibrator,
            descents,
            validator,
            models);

        await handler.HandleAsync(MakeJob(), CancellationToken.None);

        var enriched = Assert.IsAssignableFrom<IReadOnlyList<CleanedActivity>>(builder.ReceivedActivities);
        Assert.Equal(["eligible", "ineligible"], enriched.Select(activity => activity.Name));
        Assert.All(enriched.SelectMany(activity => activity.Samples), sample =>
        {
            Assert.NotEqual(123, sample.Gradient);
            Assert.NotEqual(456, sample.CurvaturePerMetre);
        });
        Assert.Same(enriched, calibrator.ReceivedActivities);
        Assert.Same(enriched, descents.ReceivedActivities);
        Assert.Same(enriched, validator.ReceivedActivities);
        Assert.All(activities.Activities.SelectMany(activity => activity.Samples), sample =>
        {
            Assert.Equal(123, sample.Gradient);
            Assert.Equal(456, sample.CurvaturePerMetre);
        });
        Assert.Equal(0, activities.SaveCount);
    }

    // Break caught: freshly persisted rows are fit once more than migrated zero-geometry rows before model evidence is built.
    [Fact]
    public async Task Handle_builds_equal_geometry_evidence_from_fresh_and_migrated_zero_rows()
    {
        var enricher = new TrainingGeometryEnricher(RouteProcessingOptions.Default);
        var raw = ActivityFixtures.CleanedFrom(ActivityFixtures.NonlinearElevationPoints());
        var fresh = enricher.Enrich(raw) with { Name = "fresh" };
        var migrated = raw with
        {
            Name = "migrated",
            Samples = raw.Samples.Select(sample => sample with { Gradient = 0, CurvaturePerMetre = 0 }).ToArray()
        };
        var activities = new FakeTrainingActivityRepository { Activities = [fresh, migrated] };
        var builder = new FakePowerModelBuilder { Result = SampleModel };
        var handler = new BuildModelJobHandler(
            new FakeProfileRepository { Profile = SampleProfile },
            activities,
            enricher,
            builder,
            new FakePhysicsCalibrator(),
            new FakeDescentLimitBuilder(),
            new FakeModelValidator(),
            new FakeRiderModelRepository());

        await handler.HandleAsync(MakeJob(), CancellationToken.None);

        var evidence = Assert.IsAssignableFrom<IReadOnlyList<CleanedActivity>>(builder.ReceivedActivities);
        Assert.Equal(
            evidence[0].Samples.Select(sample => (sample.Position.ElevationMetres, sample.Gradient, sample.CurvaturePerMetre)),
            evidence[1].Samples.Select(sample => (sample.Position.ElevationMetres, sample.Gradient, sample.CurvaturePerMetre)));
    }

    [Fact]
    public async Task Handle_saves_fallback_calibration_and_descent_results_as_a_valid_model()
    {
        var profiles = new FakeProfileRepository { Profile = SampleProfile };
        var activities = new FakeTrainingActivityRepository { Activities = ThreeEligibleActivities() };
        var calibration = new PhysicalCalibrationResult(PhysicalCoefficients.Default, false, "insufficient-physics-evidence");
        var models = new FakeRiderModelRepository();
        var handler = new BuildModelJobHandler(
            profiles,
            activities,
            new FakeTrainingGeometryEnricher(),
            new FakePowerModelBuilder { Result = SampleModel },
            new FakePhysicsCalibrator { Result = calibration },
            new FakeDescentLimitBuilder { Result = DescentLimitModel.Conservative },
            new FakeModelValidator(),
            models);

        await handler.HandleAsync(MakeJob(), CancellationToken.None);

        Assert.Equal(
            new RiderModel(SampleModel, PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "route-model-v2"),
            models.Saved!.Value.Model);
    }

    [Fact]
    public async Task Handle_throws_permanent_exception_when_profile_is_missing()
    {
        var handler = CreateHandler(profiles: new FakeProfileRepository { Profile = null });

        var exception = await Assert.ThrowsAsync<ModelBuildException>(() => handler.HandleAsync(MakeJob(), CancellationToken.None));

        Assert.Equal("profile-missing", exception.Code);
    }

    [Fact]
    public async Task Handle_throws_permanent_exception_when_no_activities_are_eligible()
    {
        var activities = new FakeTrainingActivityRepository { Activities = [IneligibleActivity()] };
        var handler = CreateHandler(activities: activities);

        var exception = await Assert.ThrowsAsync<ModelBuildException>(() => handler.HandleAsync(MakeJob(), CancellationToken.None));

        Assert.Equal("no-eligible-activities", exception.Code);
    }

    [Fact]
    public async Task Handle_throws_permanent_exception_when_the_builder_finds_no_power_evidence()
    {
        var builder = new FakePowerModelBuilder { ThrownException = new InvalidOperationException("No eligible power evidence is available.") };
        var handler = CreateHandler(builder: builder);

        var exception = await Assert.ThrowsAsync<ModelBuildException>(() => handler.HandleAsync(MakeJob(), CancellationToken.None));

        Assert.Equal("no-power-evidence", exception.Code);
    }

    [Theory]
    [InlineData(ModelValidationStatus.InsufficientData, null, null)]
    [InlineData(ModelValidationStatus.Passed, .05, .09)]
    public async Task Handle_saves_the_validator_summary_unchanged(
        ModelValidationStatus status,
        double? median,
        double? p90)
    {
        var validation = new ModelValidationSummary(status, median, p90);
        var validator = new FakeModelValidator { Result = validation };
        var models = new FakeRiderModelRepository();
        var handler = CreateHandler(validator: validator, models: models);

        await handler.HandleAsync(MakeJob(), CancellationToken.None);

        Assert.Equal(validation, models.Saved!.Value.Validation);
    }

    private static BuildModelJobHandler CreateHandler(
        FakeProfileRepository? profiles = null,
        FakeTrainingActivityRepository? activities = null,
        FakePowerModelBuilder? builder = null,
        FakeModelValidator? validator = null,
        FakeRiderModelRepository? models = null) =>
        new(
            profiles ?? new FakeProfileRepository { Profile = SampleProfile },
            activities ?? new FakeTrainingActivityRepository { Activities = ThreeEligibleActivities() },
            new FakeTrainingGeometryEnricher(),
            builder ?? new FakePowerModelBuilder { Result = SampleModel },
            new FakePhysicsCalibrator(),
            new FakeDescentLimitBuilder(),
            validator ?? new FakeModelValidator(),
            models ?? new FakeRiderModelRepository());

    private static AnalysisJob MakeJob() =>
        new(Guid.NewGuid(), JobType.BuildModel, ModelSubject.Id, JobState.Running, 1, "worker-1", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

    private static IReadOnlyList<CleanedActivity> ThreeEligibleActivities() =>
        [EligibleActivity("one"), EligibleActivity("two"), EligibleActivity("three")];

    private static CleanedActivity EligibleActivity(string name)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[] { new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51, -2, 100), 7, 200, null, null, false, 0) };
        return new CleanedActivity(name, samples, TimeSpan.FromMinutes(20), new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []), ActivityFixtures.Metadata($"{name}.fit", start, start, null, null, null, null));
    }

    private static CleanedActivity IneligibleActivity() =>
        LegacyGeometryActivity("ineligible", ActivityEligibility.Ineligible);

    private static CleanedActivity LegacyGeometryActivity(string name, ActivityEligibility eligibility)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51, -2, 100), 7, eligibility == ActivityEligibility.Eligible ? (ushort)200 : null, null, null, false, 123, 456),
            new CleanRideSample(start.AddMinutes(20), TimeSpan.FromMinutes(20), new GeoPoint(51.01, -2, 120), 7, eligibility == ActivityEligibility.Eligible ? (ushort)200 : null, null, null, false, 123, 456),
        };
        var quality = eligibility == ActivityEligibility.Eligible
            ? new ActivityQuality(eligibility, 1, 1, 1, 1, new Dictionary<string, int>(), [])
            : new ActivityQuality(eligibility, .1, .1, .1, 0, new Dictionary<string, int> { ["gap"] = 1 }, ["too-short"]);
        return new CleanedActivity(name, samples, TimeSpan.FromMinutes(20), quality, ActivityFixtures.Metadata($"{name}.fit", start, start.AddMinutes(20), null, null, null, null));
    }

    private static DescentLimitModel LearnedDescentModel() => new(
        DescentLimitModel.Conservative.Cells.Select((cell, index) =>
            index == 0
                ? cell with { Evidence = TimeSpan.FromMinutes(20), ActivityCount = 3, Confidence = ConfidenceLevel.High, IsFallback = false }
                : cell).ToArray());

    private sealed class FakeProfileRepository : IProfileRepository
    {
        public RiderProfile? Profile { get; init; }

        public Task<RiderProfile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTrainingActivityRepository : ITrainingActivityRepository
    {
        public IReadOnlyList<CleanedActivity> Activities { get; init; } = [];
        public int SaveCount { get; private set; }

        public Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(Activities);
    }

    private sealed class FakeTrainingGeometryEnricher : ITrainingGeometryEnricher
    {
        public List<CleanedActivity> Inputs { get; } = [];
        public IReadOnlyList<CleanedActivity> Outputs { get; private set; } = [];

        public CleanedActivity Enrich(CleanedActivity activity)
        {
            Inputs.Add(activity);
            var output = activity with { Name = $"enriched-{activity.Name}" };
            Outputs = [.. Outputs, output];
            return output;
        }
    }

    private sealed class FakePowerModelBuilder : IPowerModelBuilder
    {
        public PowerModel? Result { get; init; }
        public InvalidOperationException? ThrownException { get; init; }
        public IReadOnlyList<CleanedActivity>? ReceivedActivities { get; private set; }

        public PowerModel Build(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
        {
            ReceivedActivities = activities;
            if (ThrownException is not null) throw ThrownException;
            return Result!;
        }
    }

    private sealed class FakePhysicsCalibrator : IPhysicsCalibrator
    {
        public PhysicalCalibrationResult Result { get; init; } = new(PhysicalCoefficients.Default, false, "insufficient-physics-evidence");
        public IReadOnlyList<CleanedActivity>? ReceivedActivities { get; private set; }

        public PhysicalCalibrationResult Calibrate(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
        {
            ReceivedActivities = activities;
            return Result;
        }
    }

    private sealed class FakeDescentLimitBuilder : IDescentLimitBuilder
    {
        public DescentLimitModel Result { get; init; } = DescentLimitModel.Conservative;
        public IReadOnlyList<CleanedActivity>? ReceivedActivities { get; private set; }

        public DescentLimitModel Build(IReadOnlyList<CleanedActivity> activities)
        {
            ReceivedActivities = activities;
            return Result;
        }
    }

    private sealed class FakeModelValidator : IModelValidator
    {
        public ModelValidationSummary Result { get; init; } = new(ModelValidationStatus.NotValidated, null, null);
        public IReadOnlyList<CleanedActivity>? ReceivedActivities { get; private set; }

        public ModelValidationSummary Validate(RiderProfile profile, IReadOnlyList<CleanedActivity> activities)
        {
            ReceivedActivities = activities;
            return Result;
        }
    }

    private sealed class FakeRiderModelRepository : IRiderModelRepository
    {
        public (RiderModel Model, RiderProfile ProfileSnapshot, ModelValidationSummary Validation)? Saved { get; private set; }

        public Task<Guid> SaveAsync(RiderModel model, RiderProfile profileSnapshot, ModelValidationSummary validation, CancellationToken cancellationToken)
        {
            Saved = (model, profileSnapshot, validation);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<RiderModelSnapshot?> GetCurrentAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RiderModelSnapshot?> GetAsync(Guid modelId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
