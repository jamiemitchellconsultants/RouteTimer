using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;
using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;

namespace RouteTimer.Persistence.Tests;

public sealed class RepositoryRoundTripTests
{
    [Fact]
    public async Task Save_prediction_preserves_model_and_profile_snapshot()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new RouteTimerDbContext(options);
        var prediction = new PredictionEntity
        {
            Id = Guid.NewGuid(),
            ModelVersion = "model-1",
            RiderWeightKg = 75,
            BikeWeightKg = 10,
            CreatedAt = DateTimeOffset.UtcNow
        };

        context.Predictions.Add(prediction);
        await context.SaveChangesAsync();
        var loaded = await context.Predictions.SingleAsync();

        Assert.Equal("model-1", loaded.ModelVersion);
        Assert.Equal(75, loaded.RiderWeightKg);
    }

    [Fact]
    public async Task Save_upload_preserves_raw_bytes_and_content_hash()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var hash = Enumerable.Repeat((byte)7, 32).ToArray();
        context.Uploads.Add(new StoredUploadEntity { Id = Guid.NewGuid(), Kind = "fit", FileName = "ride.fit", Content = [1, 2, 3], Sha256 = hash, CreatedAt = DateTimeOffset.UtcNow });

        await context.SaveChangesAsync();
        var loaded = await context.Uploads.SingleAsync();

        Assert.Equal(hash, loaded.Sha256);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Content);
    }

    [Fact]
    public async Task Store_upload_if_absent_retains_the_first_copy_and_rejects_a_matching_hash()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new StoredUploadRepository(context);
        var hash = Enumerable.Repeat((byte)9, 32).ToArray();

        var first = await repository.StoreIfAbsentAsync(new StoredUpload(Guid.NewGuid(), "first.fit", "fit", [1, 2, 3], hash, DateTimeOffset.UtcNow), CancellationToken.None);
        var duplicate = await repository.StoreIfAbsentAsync(new StoredUpload(Guid.NewGuid(), "second.fit", "fit", [4, 5, 6], hash, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(first);
        Assert.False(duplicate);
        var saved = Assert.Single(context.Uploads);
        Assert.Equal("first.fit", saved.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.Content);
    }

    [Fact]
    public async Task Save_profile_overwrites_the_single_current_profile()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new ProfileRepository(context);

        await repository.SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
        await repository.SaveAsync(new RiderProfile(76, 11), CancellationToken.None);

        Assert.Equal(new RiderProfile(76, 11), await repository.GetAsync(CancellationToken.None));
        Assert.Single(context.Profiles);
    }

    [Fact]
    public async Task Get_upload_returns_the_stored_bytes_and_returns_null_for_an_unknown_id()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new StoredUploadRepository(context);
        var hash = Enumerable.Repeat((byte)3, 32).ToArray();
        var uploadId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        await repository.StoreIfAbsentAsync(new StoredUpload(uploadId, "ride.fit", "fit", [1, 2, 3, 4], hash, createdAt), CancellationToken.None);

        var found = await repository.GetAsync(uploadId, CancellationToken.None);
        var missing = await repository.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("ride.fit", found.FileName);
        Assert.Equal("fit", found.Kind);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, found.Content);
        Assert.Equal(hash, found.Sha256);
        Assert.Equal(createdAt, found.CreatedAt);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Save_training_activity_round_trips_samples_and_quality_summary()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new TrainingActivityRepository(context);
        var uploadId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51.1, -2.1, 100), 5.0, 180, 140, 85, false, 0.5),
            new CleanRideSample(start.AddSeconds(5), TimeSpan.FromSeconds(5), new GeoPoint(51.2, -2.2, 105), 6.0, null, 141, 86, true, -1.5),
            new CleanRideSample(start.AddSeconds(10), TimeSpan.FromSeconds(10), new GeoPoint(51.3, -2.3, 110), 7.0, 200, null, null, false, 0)
        };
        var quality = new ActivityQuality(
            ActivityEligibility.Eligible,
            PositionCoverage: 1.0,
            ElevationCoverage: 0.95,
            SpeedCoverage: 1.0,
            PowerCoverage: 0.66,
            ExclusionCounts: new Dictionary<string, int> { ["gap"] = 1, ["pause"] = 2 },
            ReasonCodes: ["low-power-coverage", "elevation-gap"]);
        var activity = new CleanedActivity("Morning Ride", samples, TimeSpan.FromSeconds(10), quality);

        var activityId = await repository.SaveAsync(uploadId, activity, CancellationToken.None);
        var loaded = await repository.GetAsync(activityId, CancellationToken.None);
        var missing = await repository.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Morning Ride", loaded.Name);
        Assert.Equal(TimeSpan.FromSeconds(10), loaded.MovingDuration);
        Assert.Equal(samples.Length, loaded.Samples.Count);
        Assert.Equal(samples, loaded.Samples);
        Assert.Equal(ActivityEligibility.Eligible, loaded.Quality.Eligibility);
        Assert.Equal(1.0, loaded.Quality.PositionCoverage);
        Assert.Equal(0.95, loaded.Quality.ElevationCoverage);
        Assert.Equal(1.0, loaded.Quality.SpeedCoverage);
        Assert.Equal(0.66, loaded.Quality.PowerCoverage);
        Assert.Equal(new Dictionary<string, int> { ["gap"] = 1, ["pause"] = 2 }, loaded.Quality.ExclusionCounts);
        Assert.Equal(new[] { "low-power-coverage", "elevation-gap" }, loaded.Quality.ReasonCodes);
        Assert.Null(missing);

        var storedActivity = await context.TrainingActivities.SingleAsync();
        Assert.Equal(uploadId, storedActivity.UploadId);
    }

    // Break caught: persistence drops enriched training curvature, so later descent learning sees every sample as straight.
    [Fact]
    public async Task Save_training_activity_round_trips_curvature()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new TrainingActivityRepository(context);
        var start = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var sample = new CleanRideSample(
            start,
            TimeSpan.Zero,
            new GeoPoint(51.1, -2.1, 100),
            12,
            180,
            140,
            85,
            false,
            -.08,
            .0125);
        var activity = new CleanedActivity(
            "Curving Descent",
            [sample],
            TimeSpan.FromSeconds(1),
            new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));

        var id = await repository.SaveAsync(Guid.NewGuid(), activity, CancellationToken.None);
        var loaded = await repository.GetAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(.0125, Assert.Single(loaded.Samples).CurvaturePerMetre, 10);
    }

    [Fact]
    public async Task GetAll_returns_every_saved_training_activity_regardless_of_eligibility()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new TrainingActivityRepository(context);
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var eligibleSamples = new[] { new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51.1, -2.1, 100), 5.0, 180, 140, 85, false, 0.5) };
        var ineligibleSamples = new[] { new CleanRideSample(start, TimeSpan.Zero, new GeoPoint(51.1, -2.1, 100), 5.0, null, null, null, false, 0.5) };
        var eligible = new CleanedActivity("Eligible Ride", eligibleSamples, TimeSpan.FromMinutes(20),
            new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));
        var ineligible = new CleanedActivity("Ineligible Ride", ineligibleSamples, TimeSpan.FromMinutes(5),
            new ActivityQuality(ActivityEligibility.Ineligible, 0.1, 0.1, 0.1, 0, new Dictionary<string, int> { ["gap"] = 3 }, ["low-coverage"]));

        await repository.SaveAsync(Guid.NewGuid(), eligible, CancellationToken.None);
        await repository.SaveAsync(Guid.NewGuid(), ineligible, CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, activity => activity.Name == "Eligible Ride" && activity.Quality.Eligibility == ActivityEligibility.Eligible);
        Assert.Contains(all, activity => activity.Name == "Ineligible Ride" && activity.Quality.Eligibility == ActivityEligibility.Ineligible);
    }

    [Fact]
    public async Task Exclusion_counts_value_comparer_is_order_independent()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var property = context.Model
            .FindEntityType(typeof(TrainingActivityEntity))!
            .FindProperty(nameof(TrainingActivityEntity.ExclusionCounts))!;
        var comparer = property.GetValueComparer();

        var inOneOrder = new Dictionary<string, int> { ["gap"] = 1, ["pause"] = 2, ["dropout"] = 3 };
        var inAnotherOrder = new Dictionary<string, int> { ["dropout"] = 3, ["gap"] = 1, ["pause"] = 2 };

        Assert.True(comparer.Equals(inOneOrder, inAnotherOrder));
        Assert.Equal(comparer.GetHashCode(inOneOrder), comparer.GetHashCode(inAnotherOrder));
    }

    // Break caught: rider-model persistence stores only power/coefficient data and loses immutable descent cells or calibration provenance.
    [Fact]
    public async Task Save_rider_model_round_trips_calibration_and_all_descent_cells()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var profile = new RiderProfile(75, 10);
        var bands = new[]
        {
            new PowerBand("flat", "short", 250, TimeSpan.FromMinutes(30), 5, 0.8, ConfidenceLevel.High),
            new PowerBand("climb", "long", 210, TimeSpan.FromMinutes(45), 3, 0.6, ConfidenceLevel.Medium),
            new PowerBand("descent", "short", 90, TimeSpan.FromMinutes(5), 1, 0.2, ConfidenceLevel.Low)
        };
        var powerModel = new PowerModel(bands, 220);
        var descentLimits = new DescentLimitModel(DescentLimitModel.Conservative.Cells
            .Select((cell, index) => cell with
            {
                SpeedCapMetresPerSecond = index == 8 ? .75 : 10 + index,
                Evidence = TimeSpan.FromSeconds(60 + index),
                ActivityCount = index + 1,
                Confidence = index % 2 == 0 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                IsFallback = index == 0
            })
            .ToArray());
        var riderModel = new RiderModel(powerModel, PhysicalCoefficients.Default, descentLimits, true, "v1");
        var validation = new ModelValidationSummary(ModelValidationStatus.NotValidated, null, null);

        var modelId = await repository.SaveAsync(riderModel, profile, validation, CancellationToken.None);
        var loaded = await repository.GetAsync(modelId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(modelId, loaded.Id);
        Assert.Equal(profile, loaded.ProfileSnapshot);
        Assert.True(loaded.WasCalibrated);
        Assert.True(loaded.DescentWasLearned);
        Assert.Equal(validation, loaded.Validation);
        Assert.Equal("v1", loaded.Model.AlgorithmVersion);
        Assert.Equal(PhysicalCoefficients.Default, loaded.Model.Coefficients);
        Assert.Equal(220, loaded.Model.PowerModel.GlobalTypicalWatts);
        Assert.Equal(bands.Length, loaded.Model.PowerModel.Bands.Count);
        foreach (var band in bands)
        {
            var loadedBand = Assert.Single(loaded.Model.PowerModel.Bands, b => b.GradeKey == band.GradeKey && b.DurationKey == band.DurationKey);
            Assert.Equal(band, loadedBand);
        }
        Assert.Equal(9, loaded.Model.DescentLimits.Cells.Count);
        Assert.Equal(descentLimits.Cells, loaded.Model.DescentLimits.Cells);
        Assert.Equal(.75, loaded.Model.DescentLimits.Cells[^1].SpeedCapMetresPerSecond);
    }

    // Break caught: malformed normalized cells are allowed to leak parser/domain exceptions or construct an invalid aggregate.
    [Theory]
    [InlineData("EvidenceSeconds", "-1")]
    [InlineData("SpeedCapMetresPerSecond", "NaN")]
    public async Task Get_rider_model_rejects_malformed_persisted_descent_cells(string propertyName, string value)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var model = new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1");
        var id = await repository.SaveAsync(model, new RiderProfile(75, 10), new ModelValidationSummary(ModelValidationStatus.NotValidated, null, null), CancellationToken.None);
        var cell = await context.RiderModelDescentLimits.FirstAsync();
        switch (propertyName)
        {
            case "Confidence": cell.Confidence = value; break;
            case "EvidenceSeconds": cell.EvidenceSeconds = double.Parse(value); break;
            case "SpeedCapMetresPerSecond": cell.SpeedCapMetresPerSecond = double.NaN; break;
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetAsync(id, CancellationToken.None));
    }

    // Break caught: Enum.TryParse accepts numeric or whitespace-normalized confidence text that was never canonically persisted.
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("+1")]
    [InlineData("low")]
    [InlineData("LOW")]
    [InlineData("Low ")]
    [InlineData(" Low")]
    [InlineData("Unknown")]
    [InlineData("Medium, High")]
    public async Task Get_rider_model_rejects_noncanonical_persisted_descent_confidence(string confidence)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var model = new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1");
        var id = await repository.SaveAsync(model, new RiderProfile(75, 10), new ModelValidationSummary(ModelValidationStatus.NotValidated, null, null), CancellationToken.None);
        var cell = await context.RiderModelDescentLimits.FirstAsync();
        cell.Confidence = confidence;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetAsync(id, CancellationToken.None));
    }

    // Break caught: stored provenance disagrees with immutable cells and callers receive contradictory snapshot metadata.
    [Fact]
    public async Task Get_rider_model_rejects_descent_learned_flag_that_disagrees_with_cells()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var model = new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1");
        var id = await repository.SaveAsync(model, new RiderProfile(75, 10), new ModelValidationSummary(ModelValidationStatus.NotValidated, null, null), CancellationToken.None);
        var entity = await context.RiderModels.SingleAsync();
        entity.DescentWasLearned = true;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task GetCurrent_returns_the_most_recently_saved_rider_model()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);
        var profile = new RiderProfile(75, 10);
        var validation = new ModelValidationSummary(ModelValidationStatus.InsufficientData, null, null);

        var firstModel = new RiderModel(new PowerModel([], 100), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1");
        var firstId = await repository.SaveAsync(firstModel, profile, validation, CancellationToken.None);

        var secondModel = new RiderModel(new PowerModel([], 150), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v2");
        var secondId = await repository.SaveAsync(secondModel, profile, validation, CancellationToken.None);

        var current = await repository.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal(secondId, current.Id);
        Assert.NotEqual(firstId, current.Id);
        Assert.Equal("v2", current.Model.AlgorithmVersion);
        Assert.Equal(150, current.Model.PowerModel.GlobalTypicalWatts);
    }

    [Fact]
    public async Task GetCurrent_and_Get_return_null_when_no_matching_rider_model_exists()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new RiderModelRepository(context);

        var current = await repository.GetCurrentAsync(CancellationToken.None);
        var missing = await repository.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(current);
        Assert.Null(missing);
    }
}
