using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Tests;

public sealed class PredictionRepositoryTests
{
    // Break caught: queued prediction and durable job are not committed together, or duplicate uploads prevent history rows.
    [Fact]
    public async Task CreateQueued_reuses_gpx_and_creates_a_distinct_prediction_and_job_for_each_submission()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var model = await SaveModelAsync(context);
        var repository = new PredictionRepository(context);
        var first = await repository.CreateQueuedAsync(Creation(model), CancellationToken.None);
        var second = await repository.CreateQueuedAsync(Creation(model), CancellationToken.None);

        Assert.NotEqual(first.PredictionId, second.PredictionId);
        Assert.Equal(1, await context.Uploads.CountAsync(upload => upload.Kind == "gpx"));
        Assert.Equal(2, await context.Predictions.CountAsync());
        Assert.Equal(2, await context.Jobs.CountAsync(job => job.Type == "PredictRoute"));
        Assert.All(await context.Jobs.ToListAsync(), job => Assert.Contains(job.SubjectId, new[] { first.PredictionId, second.PredictionId }));
    }

    // Break caught: completed results lose segment order or summary projections unnecessarily materialize segments.
    [Fact]
    public async Task Publish_round_trips_ordered_detail_but_summary_omits_segments()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var model = await SaveModelAsync(context);
        var repository = new PredictionRepository(context);
        var created = await repository.CreateQueuedAsync(Creation(model), CancellationToken.None);
        await repository.PublishAsync(created.PredictionId, new PredictionPublication(100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, ["default-coefficients"],
            [new PersistedPredictionSegment(2, 51.2, -2.2, 110, 100, 25, .05, .001, 200, 5, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), ConfidenceLevel.Medium),
             new PersistedPredictionSegment(1, 51.1, -2.1, 105, 75, 25, .04, .002, 190, 4, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), ConfidenceLevel.High)]), CancellationToken.None);

        var summary = Assert.Single(await repository.GetSummariesAsync(CancellationToken.None));
        var detail = await repository.GetAsync(created.PredictionId, CancellationToken.None);

        Assert.Empty(summary.Segments);
        Assert.NotNull(detail);
        Assert.Equal(new[] { 1, 2 }, detail.Segments.Select(segment => segment.Sequence));
        Assert.Equal(TimeSpan.FromSeconds(20), detail.MovingTime);
        Assert.Null(await repository.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static QueuedPredictionCreation Creation(RiderModelSnapshot model) => new(
        new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", [1, 2, 3], Enumerable.Repeat((byte)9, 32).ToArray(), DateTimeOffset.UtcNow),
        model,
        new RiderProfile(75, 10),
        PredictionAssumptions.RoadCalmDryMovingOnly,
        DateTimeOffset.UtcNow);

    private static async Task<RiderModelSnapshot> SaveModelAsync(RouteTimerDbContext context)
    {
        var models = new RiderModelRepository(context);
        var profile = new RiderProfile(75, 10);
        var validation = new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08);
        var id = await models.SaveAsync(new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, "v1"), profile, false, validation, CancellationToken.None);
        return (await models.GetAsync(id, CancellationToken.None))!;
    }
}
