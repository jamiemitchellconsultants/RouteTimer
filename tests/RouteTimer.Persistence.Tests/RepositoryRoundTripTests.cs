using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;
using RouteTimer.Domain.Activities;
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
}
