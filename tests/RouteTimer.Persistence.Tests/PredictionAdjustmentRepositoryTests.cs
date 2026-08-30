using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests;

public sealed class PredictionAdjustmentRepositoryTests
{
    // Break caught: an adjustment can be created under a baseline that hasn't succeeded yet, or that never existed.
    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task CreateQueued_rejects_a_baseline_that_has_not_succeeded(string baselineState)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var predictionId = await SeedBaselineAsync(context, baselineState);
        var repository = new PredictionAdjustmentRepository(context);

        var result = await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None);

        Assert.Equal(AdjustmentBaselineStatus.BaselineNotReady, result.BaselineStatus);
        Assert.Null(result.AdjustmentId);
        Assert.Equal(0, await context.PredictionAdjustments.CountAsync());
    }

    [Fact]
    public async Task CreateQueued_rejects_a_missing_baseline()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var repository = new PredictionAdjustmentRepository(context);

        var result = await repository.CreateQueuedAsync(Creation(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(AdjustmentBaselineStatus.BaselineNotFound, result.BaselineStatus);
        Assert.Null(result.AdjustmentId);
    }

    // Break caught: the canonical strategy JSON is re-serialized or mutated on the way into storage.
    [Fact]
    public async Task CreateQueued_under_a_succeeded_baseline_preserves_canonical_strategy_json_exactly()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var predictionId = await SeedBaselineAsync(context, "Succeeded");
        var repository = new PredictionAdjustmentRepository(context);
        const string canonicalJson = """{"type":"timeTarget","targetMovingSeconds":1200}""";

        var result = await repository.CreateQueuedAsync(Creation(predictionId, strategyJson: canonicalJson), CancellationToken.None);

        Assert.Equal(AdjustmentBaselineStatus.Ready, result.BaselineStatus);
        var stored = await context.PredictionAdjustments.AsNoTracking().SingleAsync(entity => entity.Id == result.AdjustmentId);
        Assert.Equal(canonicalJson, stored.StrategyJson);
        Assert.Equal(AdjustmentState.Queued.ToString(), stored.State);
    }

    // Break caught: listing or fetching an adjustment leaks across baselines, or list order drifts from newest-first.
    [Fact]
    public async Task Summaries_are_newest_first_and_detail_matches_only_when_both_ids_agree()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var predictionId = await SeedBaselineAsync(context, "Succeeded");
        var otherPredictionId = await SeedBaselineAsync(context, "Succeeded");
        var repository = new PredictionAdjustmentRepository(context);
        var first = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
        var second = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
        var otherBaselineAdjustment = (await repository.CreateQueuedAsync(Creation(otherPredictionId), CancellationToken.None)).AdjustmentId!.Value;

        var summaries = await repository.GetSummariesAsync(predictionId, CancellationToken.None);

        Assert.Equal([second, first], summaries.Select(summary => summary.Id));
        Assert.NotNull(await repository.GetAsync(predictionId, first, CancellationToken.None));
        Assert.Null(await repository.GetAsync(predictionId, otherBaselineAdjustment, CancellationToken.None));
        Assert.Null(await repository.GetAsync(otherPredictionId, first, CancellationToken.None));
    }

    // Break caught: publication only partially persists summary/report/warnings/annotations/segments.
    [Fact]
    public async Task Publish_persists_summary_report_warnings_and_segment_annotations_atomically()
    {
        await using var database = await StartDatabaseAsync();
        Guid predictionId, adjustmentId, jobId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            predictionId = await SeedBaselineAsync(setup, "Succeeded", sequences: [1, 2]);
            var repository = new PredictionAdjustmentRepository(setup);
            adjustmentId = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
            jobId = await EnqueueRunningJobAsync(setup, adjustmentId, "worker-a");
        }

        await using (var publishing = CreateContext(database))
        {
            var published = await new PredictionAdjustmentRepository(publishing).TryPublishAsync(
                adjustmentId, jobId, "worker-a",
                new AdjustmentPublication(
                    TimeSpan.FromSeconds(30), 6, 210, ConfidenceLevel.Medium, ["segment-gains-power-clamped"], """{"type":"segmentSpecificGains"}""", "segment-gains-v1",
                    [
                        new PersistedAdjustmentSegment(1, 200, 5, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), ConfidenceLevel.High, null, "burn", 12000),
                        new PersistedAdjustmentSegment(2, 220, 7, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), ConfidenceLevel.Medium, 3, null, null),
                    ]),
                CancellationToken.None);
            Assert.True(published);
        }

        await using var verify = CreateContext(database);
        var detail = await new PredictionAdjustmentRepository(verify).GetAsync(predictionId, adjustmentId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(AdjustmentState.Succeeded, detail.State);
        Assert.Equal(TimeSpan.FromSeconds(30), detail.MovingTime);
        Assert.Equal(6, detail.AverageSpeedMetresPerSecond);
        Assert.Equal(210, detail.AveragePowerWatts);
        Assert.Equal(["segment-gains-power-clamped"], detail.Warnings);
        // jsonb re-serializes on write (e.g. adds a space after ':'), so compare parsed structure, not raw bytes.
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"type":"segmentSpecificGains"}""").RootElement,
            JsonDocument.Parse(detail.ResultJson!).RootElement));
        Assert.Equal(new[] { 1, 2 }, detail.Segments.Select(segment => segment.Sequence));
        Assert.Equal("burn", detail.Segments[0].StrategyPhase);
        Assert.Equal(12000, detail.Segments[0].WPrimeBalanceJoules);
        Assert.Equal(3, detail.Segments[1].ZoneNumber);
    }

    // Break caught: publishing a segment set that doesn't match the baseline's sequences is accepted.
    [Fact]
    public async Task Publish_rejects_a_segment_set_that_differs_from_the_baseline_sequences()
    {
        await using var database = await StartDatabaseAsync();
        Guid predictionId, adjustmentId, jobId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            predictionId = await SeedBaselineAsync(setup, "Succeeded", sequences: [1, 2]);
            var repository = new PredictionAdjustmentRepository(setup);
            adjustmentId = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
            jobId = await EnqueueRunningJobAsync(setup, adjustmentId, "worker-a");
        }

        await using var publishing = CreateContext(database);
        var published = await new PredictionAdjustmentRepository(publishing).TryPublishAsync(
            adjustmentId, jobId, "worker-a",
            new AdjustmentPublication(TimeSpan.FromSeconds(10), 5, 200, ConfidenceLevel.Medium, [], "{}", "time-target-v1",
                [new PersistedAdjustmentSegment(1, 200, 5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), ConfidenceLevel.Medium, null, null, null)]),
            CancellationToken.None);

        Assert.False(published);
        await using var verify = CreateContext(database);
        var adjustment = await verify.PredictionAdjustments.AsNoTracking().SingleAsync(entity => entity.Id == adjustmentId);
        Assert.Equal(AdjustmentState.Queued.ToString(), adjustment.State);
    }

    // Break caught: a job for the wrong adjustment, wrong worker, or not currently running can still publish.
    [Fact]
    public async Task Publish_requires_matching_running_job_and_worker()
    {
        await using var database = await StartDatabaseAsync();
        Guid predictionId, adjustmentId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            predictionId = await SeedBaselineAsync(setup, "Succeeded", sequences: [1]);
            adjustmentId = (await new PredictionAdjustmentRepository(setup).CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
        }

        await using var verify = CreateContext(database);
        var published = await new PredictionAdjustmentRepository(verify).TryPublishAsync(
            adjustmentId, Guid.NewGuid(), "worker-a", SinglePublication(), CancellationToken.None);

        Assert.False(published);
    }

    // Break caught: deleting one child touches the baseline or a sibling adjustment.
    [Fact]
    public async Task Delete_removes_only_the_targeted_child_and_cancels_its_job()
    {
        await using var database = await StartDatabaseAsync();
        Guid predictionId, keep, delete, jobId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            predictionId = await SeedBaselineAsync(setup, "Succeeded", sequences: [1]);
            var repository = new PredictionAdjustmentRepository(setup);
            keep = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
            delete = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
            jobId = await EnqueueRunningJobAsync(setup, delete, "worker-a");
        }

        await using (var deleting = CreateContext(database))
        {
            Assert.True(await new PredictionAdjustmentRepository(deleting).DeleteAsync(predictionId, delete, DateTimeOffset.UtcNow, CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        Assert.Null(await verify.PredictionAdjustments.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == delete));
        Assert.NotNull(await verify.PredictionAdjustments.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == keep));
        Assert.NotNull(await verify.Predictions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == predictionId));
        var job = await verify.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == jobId);
        Assert.Equal("Cancelled", job.State);
    }

    // Break caught: deleting the baseline leaves its adjustments (and their segments) behind instead of cascading.
    // A real cascade only fires at the database's FK level, so this needs PostgreSQL, not the InMemory provider.
    [Fact]
    public async Task Deleting_the_baseline_cascades_its_adjustments_and_segments()
    {
        await using var database = await StartDatabaseAsync();
        Guid predictionId, adjustmentId, jobId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            predictionId = await SeedBaselineAsync(setup, "Succeeded", sequences: [1]);
            var repository = new PredictionAdjustmentRepository(setup);
            adjustmentId = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;
            jobId = await EnqueueRunningJobAsync(setup, adjustmentId, "worker-a");
            await new PredictionAdjustmentRepository(setup).TryPublishAsync(adjustmentId, jobId, "worker-a", SinglePublication(), CancellationToken.None);
        }

        await using (var deleting = CreateContext(database))
        {
            Assert.True(await new PredictionRepository(deleting).DeleteAsync(predictionId, DateTimeOffset.UtcNow, CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        Assert.Equal(0, await verify.PredictionAdjustments.CountAsync(entity => entity.Id == adjustmentId));
        Assert.Empty(await verify.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM prediction_adjustment_segments WHERE "AdjustmentId" = {adjustmentId}
            """).ToListAsync());
    }

    // Break caught: FailAsync leaves an adjustment in a non-terminal state or drops its diagnostic.
    [Fact]
    public async Task Fail_moves_the_adjustment_to_a_terminal_failed_state()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var predictionId = await SeedBaselineAsync(context, "Succeeded");
        var repository = new PredictionAdjustmentRepository(context);
        var adjustmentId = (await repository.CreateQueuedAsync(Creation(predictionId), CancellationToken.None)).AdjustmentId!.Value;

        await repository.FailAsync(adjustmentId, "invalid-prediction-adjustment-result", "boom", CancellationToken.None);

        var stored = await context.PredictionAdjustments.AsNoTracking().SingleAsync(entity => entity.Id == adjustmentId);
        Assert.Equal(AdjustmentState.Failed.ToString(), stored.State);
        Assert.NotNull(stored.CompletedAt);
        Assert.Contains("boom", Assert.Single(stored.Warnings));
    }

    private static QueuedAdjustmentCreation Creation(Guid predictionId, string? strategyJson = null) => new(
        predictionId, PacingStrategyType.TimeTarget, strategyJson ?? """{"type":"timeTarget"}""", DateTimeOffset.UtcNow);

    private static AdjustmentPublication SinglePublication() => new(
        TimeSpan.FromSeconds(10), 5, 200, ConfidenceLevel.Medium, [], "{}", "time-target-v1",
        [new PersistedAdjustmentSegment(1, 200, 5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), ConfidenceLevel.Medium, null, null, null)]);

    private static async Task<Guid> SeedBaselineAsync(RouteTimerDbContext context, string state, int[]? sequences = null)
    {
        var models = new RiderModelRepository(context);
        var profile = new RiderProfile(75, 10);
        var validation = new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08);
        var modelId = await models.SaveAsync(new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1"), profile, validation, CancellationToken.None);
        var model = (await models.GetAsync(modelId, CancellationToken.None))!;

        var predictions = new PredictionRepository(context);
        var created = await predictions.CreateQueuedAsync(new QueuedPredictionCreation(
            new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", [1, 2, 3], Enumerable.Repeat((byte)9, 32).ToArray(), DateTimeOffset.UtcNow),
            model, profile, PredictionAssumptions.RoadCalmDryMovingOnly, DateTimeOffset.UtcNow), CancellationToken.None);

        if (state == "Queued") return created.PredictionId;

        var job = await context.Jobs.SingleAsync(entity => entity.Id == created.JobId);
        job.State = "Running";
        job.WorkerId = "seed-worker";
        await context.SaveChangesAsync();

        if (state == "Running") return created.PredictionId;

        if (state is "Failed" or "Cancelled")
        {
            var prediction = await context.Predictions.SingleAsync(entity => entity.Id == created.PredictionId);
            prediction.State = state;
            prediction.CompletedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
            return created.PredictionId;
        }

        var segments = (sequences ?? [1]).Select(sequence => new PersistedPredictionSegment(
            sequence, 51.1, -2.1, 100, sequence * 100, 100, .02, 0, 200, 5, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20 * sequence), ConfidenceLevel.Medium)).ToList();
        await predictions.TryPublishAsync(created.PredictionId, created.JobId, "seed-worker",
            new PredictionPublication(sequences?.Length * 100 ?? 100, 5, TimeSpan.FromSeconds(20 * (sequences?.Length ?? 1)), 5, 200, ConfidenceLevel.Medium, [], segments),
            CancellationToken.None);
        return created.PredictionId;
    }

    private static async Task<Guid> EnqueueRunningJobAsync(RouteTimerDbContext context, Guid adjustmentId, string workerId)
    {
        var job = new AnalysisJobEntity
        {
            Id = Guid.NewGuid(),
            Type = "AdjustPrediction",
            SubjectId = adjustmentId,
            State = "Running",
            ProgressPercent = 0,
            ProgressStage = "running",
            WorkerId = workerId,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        context.Jobs.Add(job);
        await context.SaveChangesAsync();
        return job.Id;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        return database;
    }

    private static RouteTimerDbContext CreateContext(PostgreSqlContainer database)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options;
        return new RouteTimerDbContext(options);
    }
}
