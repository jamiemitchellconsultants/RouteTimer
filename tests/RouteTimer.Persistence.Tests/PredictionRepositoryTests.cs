using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Entities;
using RouteTimer.Persistence.Jobs;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;
using Testcontainers.PostgreSql;

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
        var job = await context.Jobs.SingleAsync(entity => entity.Id == created.JobId);
        job.State = "Running";
        job.WorkerId = "worker-a";
        await context.SaveChangesAsync();
        Assert.True(await repository.TryPublishAsync(created.PredictionId, created.JobId, "worker-a", new PredictionPublication(100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, ["default-coefficients"],
            [new PersistedPredictionSegment(2, 51.2, -2.2, 110, 100, 25, .05, .001, 200, 5, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), ConfidenceLevel.Medium),
             new PersistedPredictionSegment(1, 51.1, -2.1, 105, 75, 25, .04, .002, 190, 4, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), ConfidenceLevel.High)]), CancellationToken.None));

        var summary = Assert.Single(await repository.GetSummariesAsync(CancellationToken.None));
        var detail = await repository.GetAsync(created.PredictionId, CancellationToken.None);

        Assert.Empty(summary.Segments);
        Assert.NotNull(detail);
        Assert.Equal(new[] { 1, 2 }, detail.Segments.Select(segment => segment.Sequence));
        Assert.Equal(TimeSpan.FromSeconds(20), detail.MovingTime);
        Assert.Null(await repository.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Publish_requires_matching_running_job_and_worker()
    {
        await using var database = await StartDatabaseAsync();
        Guid predictionId;
        Guid jobId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            var model = await SaveModelAsync(setup);
            var created = await new PredictionRepository(setup).CreateQueuedAsync(Creation(model), CancellationToken.None);
            predictionId = created.PredictionId;
            jobId = created.JobId;
        }

        await using (var unpublished = CreateContext(database))
        {
            Assert.False(await new PredictionRepository(unpublished).TryPublishAsync(predictionId, jobId, "worker-a", Publication(), CancellationToken.None));
        }

        await using (var claimed = CreateContext(database))
        {
            var queue = new PostgresJobQueue(claimed, TimeProvider.System);
            Assert.NotNull(await queue.ClaimAsync("worker-b", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        var repository = new PredictionRepository(verify);
        Assert.False(await repository.TryPublishAsync(predictionId, jobId, "worker-a", Publication(), CancellationToken.None));

        var prediction = await verify.Predictions.AsNoTracking().SingleAsync(entity => entity.Id == predictionId);
        var job = await verify.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == jobId);
        Assert.Equal(PredictionState.Queued.ToString(), prediction.State);
        Assert.Empty(await verify.PredictionSegments.AsNoTracking().Where(entity => entity.PredictionId == predictionId).ToListAsync());
        Assert.Equal(JobState.Running.ToString(), job.State);
        Assert.Equal("worker-b", job.WorkerId);
    }

    // Break caught: deleting a prediction can leave its claimed job or retained upload behind, or let partial rows survive.
    [Fact]
    public async Task Delete_cancels_active_job_and_removes_segments_prediction_and_gpx_atomically()
    {
        await using var database = await StartDatabaseAsync();
        var deletedAt = new DateTimeOffset(2026, 8, 25, 14, 0, 0, TimeSpan.Zero);
        Guid predictionId;
        Guid jobId;
        Guid uploadId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            var model = await SaveModelAsync(setup);
            var created = await new PredictionRepository(setup).CreateQueuedAsync(Creation(model), CancellationToken.None);
            predictionId = created.PredictionId;
            jobId = created.JobId;
            uploadId = (await setup.Predictions.AsNoTracking().SingleAsync(entity => entity.Id == predictionId)).UploadId;

            var queue = new PostgresJobQueue(setup, TimeProvider.System);
            Assert.NotNull(await queue.ClaimAsync("worker-a", deletedAt.AddMinutes(-2), TimeSpan.FromMinutes(2), CancellationToken.None));
            Assert.True(await queue.ReportProgressAsync(jobId, "worker-a", 45, "running", deletedAt.AddMinutes(-1), CancellationToken.None));

            setup.PredictionSegments.Add(new PredictionSegmentEntity
            {
                PredictionId = predictionId,
                Sequence = 1,
                Latitude = 51.1,
                Longitude = -2.1,
                ElevationMetres = 100,
                CumulativeDistanceMetres = 100,
                SegmentDistanceMetres = 100,
                Gradient = .04,
                CurvaturePerMetre = .001,
                PredictedPowerWatts = 200,
                PredictedSpeedMetresPerSecond = 5,
                SegmentMovingSeconds = 20,
                CumulativeMovingSeconds = 20,
                Confidence = ConfidenceLevel.Medium.ToString()
            });
            await setup.SaveChangesAsync();
        }

        await using (var deleting = CreateContext(database))
        {
            Assert.True(await new PredictionRepository(deleting).DeleteAsync(predictionId, deletedAt, CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        var job = await verify.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == jobId);
        Assert.Equal(JobState.Cancelled.ToString(), job.State);
        Assert.Equal(45, job.ProgressPercent);
        Assert.Equal("cancelled", job.ProgressStage);
        Assert.Equal(deletedAt, job.CompletedAt);
        Assert.Null(job.WorkerId);
        Assert.Null(job.LeaseExpiresAt);
        Assert.Null(await verify.Predictions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == predictionId));
        Assert.Empty(await verify.PredictionSegments.AsNoTracking().Where(entity => entity.PredictionId == predictionId).ToListAsync());
        Assert.Null(await verify.Uploads.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == uploadId));
    }

    // Break caught: prediction deletion removes the immutable rider-model snapshot or its shared retained GPX while another prediction still references them.
    [Fact]
    public async Task Delete_keeps_referenced_immutable_rider_model()
    {
        await using var database = await StartDatabaseAsync();
        Guid firstPredictionId;
        Guid secondPredictionId;
        Guid riderModelId;
        Guid sharedUploadId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            var model = await SaveModelAsync(setup);
            riderModelId = model.Id;
            var repository = new PredictionRepository(setup);
            firstPredictionId = (await repository.CreateQueuedAsync(Creation(model), CancellationToken.None)).PredictionId;
            secondPredictionId = (await repository.CreateQueuedAsync(Creation(model), CancellationToken.None)).PredictionId;
            sharedUploadId = (await setup.Predictions.AsNoTracking().SingleAsync(entity => entity.Id == firstPredictionId)).UploadId;
        }

        await using (var deleting = CreateContext(database))
        {
            Assert.True(await new PredictionRepository(deleting).DeleteAsync(firstPredictionId, DateTimeOffset.UtcNow, CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        Assert.Null(await verify.Predictions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == firstPredictionId));
        Assert.NotNull(await verify.Predictions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == secondPredictionId));
        Assert.NotNull(await verify.RiderModels.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == riderModelId));
        Assert.NotNull(await verify.Uploads.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == sharedUploadId));
    }

    // Break caught: a worker that finishes after the delete wins can still recreate the prediction result by publishing late.
    [Fact]
    public async Task Late_worker_cannot_publish_after_delete()
    {
        await using var database = await StartDatabaseAsync();
        Guid predictionId;
        Guid jobId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            var model = await SaveModelAsync(setup);
            var created = await new PredictionRepository(setup).CreateQueuedAsync(Creation(model), CancellationToken.None);
            predictionId = created.PredictionId;
            jobId = created.JobId;
            Assert.NotNull(await new PostgresJobQueue(setup, TimeProvider.System).ClaimAsync("worker-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None));
        }

        await using (var deleting = CreateContext(database))
        {
            Assert.True(await new PredictionRepository(deleting).DeleteAsync(predictionId, DateTimeOffset.UtcNow, CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        var published = await new PredictionRepository(verify).TryPublishAsync(predictionId, jobId, "worker-a", Publication(), CancellationToken.None);

        Assert.False(published);
        Assert.Null(await verify.Predictions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == predictionId));
        var job = await verify.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == jobId);
        Assert.Equal(JobState.Cancelled.ToString(), job.State);
        Assert.Empty(await verify.PredictionSegments.AsNoTracking().Where(entity => entity.PredictionId == predictionId).ToListAsync());
    }

    // Break caught: deleting a missing prediction can still mutate unrelated retained uploads or prediction jobs.
    [Fact]
    public async Task Missing_delete_returns_false_without_mutation()
    {
        await using var database = await StartDatabaseAsync();
        Guid existingPredictionId;
        Guid existingJobId;
        Guid uploadId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            var model = await SaveModelAsync(setup);
            var created = await new PredictionRepository(setup).CreateQueuedAsync(Creation(model), CancellationToken.None);
            existingPredictionId = created.PredictionId;
            existingJobId = created.JobId;
            uploadId = (await setup.Predictions.AsNoTracking().SingleAsync(entity => entity.Id == existingPredictionId)).UploadId;
        }

        await using (var deleting = CreateContext(database))
        {
            Assert.False(await new PredictionRepository(deleting).DeleteAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        Assert.NotNull(await verify.Predictions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == existingPredictionId));
        Assert.NotNull(await verify.Uploads.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == uploadId));
        var job = await verify.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == existingJobId);
        Assert.Equal(JobState.Queued.ToString(), job.State);
    }

    // Break caught: concurrent identical GPX submissions race through the pre-insert lookup and one surfaces a unique-constraint server error.
    [Fact]
    public async Task Concurrent_creates_reuse_one_gpx_upload_and_create_one_prediction_and_job_per_submission()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        RiderModelSnapshot model;
        await using (var seed = new RouteTimerDbContext(options))
        {
            await seed.Database.MigrateAsync();
            model = await SaveModelAsync(seed);
        }

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = new RouteTimerDbContext(options);
            return await new PredictionRepository(context).CreateQueuedAsync(Creation(model), CancellationToken.None);
        });

        var created = await Task.WhenAll(tasks);

        Assert.Equal(8, created.Select(result => result.PredictionId).Distinct().Count());
        await using var verify = new RouteTimerDbContext(options);
        Assert.Equal(1, await verify.Uploads.CountAsync(upload => upload.Kind == "gpx"));
        Assert.Equal(8, await verify.Predictions.CountAsync());
        Assert.Equal(8, await verify.Jobs.CountAsync(job => job.Type == "PredictRoute"));
    }

    // Break caught: submission history reads profile/model from current rows after they change instead of the persisted prediction snapshots.
    [Fact]
    public async Task Submission_round_trips_its_original_profile_and_model_snapshot_after_current_values_change()
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new RouteTimerDbContext(options);
        var profiles = new ProfileRepository(context);
        await profiles.SaveAsync(new RiderProfile(75, 10), CancellationToken.None);
        var models = new RiderModelRepository(context);
        var originalModelId = await models.SaveAsync(new RiderModel(new PowerModel([], 210), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1"), new RiderProfile(75, 10),
            new ModelValidationSummary(ModelValidationStatus.InsufficientData, null, null), CancellationToken.None);
        var submissions = new PredictionSubmissionService(profiles, models, new PredictionRepository(context), TimeProvider.System);

        var created = await submissions.SubmitAsync(new PredictionUpload("route.gpx", new MemoryStream([1, 2, 3])), CancellationToken.None);
        await profiles.SaveAsync(new RiderProfile(80, 11), CancellationToken.None);
        await models.SaveAsync(new RiderModel(new PowerModel([], 260), PhysicalCoefficients.Default, DescentLimitModel.Conservative, true, "v2"), new RiderProfile(80, 11),
            new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08), CancellationToken.None);

        var detail = await new PredictionRepository(context).GetAsync(created.PredictionId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(originalModelId, detail.ModelId);
        Assert.Equal("v1", detail.ModelVersion);
        Assert.Equal(new RiderProfile(75, 10), detail.Profile);
        Assert.Equal(ModelValidationStatus.InsufficientData, detail.Validation.Status);
    }

    // Break caught: a failure after the raw upload insert leaves an upload or prediction committed despite the submission transaction failing.
    [Fact]
    public async Task CreateQueued_rolls_back_the_upload_prediction_and_job_when_job_insert_fails()
    {
        await using var database = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>().UseNpgsql(database.GetConnectionString()).Options;
        RiderModelSnapshot model;
        await using (var setup = new RouteTimerDbContext(options))
        {
            await setup.Database.MigrateAsync();
            model = await SaveModelAsync(setup);
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_prediction_job() RETURNS trigger AS $$
                BEGIN
                    IF NEW."Type" = 'PredictRoute' THEN RAISE EXCEPTION 'forced-prediction-job-failure'; END IF;
                    RETURN NEW;
                END $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_prediction_job BEFORE INSERT ON analysis_jobs FOR EACH ROW EXECUTE FUNCTION reject_prediction_job();
                """);
        }

        await using (var failing = new RouteTimerDbContext(options))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => new PredictionRepository(failing).CreateQueuedAsync(Creation(model), CancellationToken.None));
        }

        await using var verify = new RouteTimerDbContext(options);
        Assert.Empty(await verify.Uploads.ToListAsync());
        Assert.Empty(await verify.Predictions.ToListAsync());
        Assert.Empty(await verify.Jobs.ToListAsync());
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
        var id = await models.SaveAsync(new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1"), profile, validation, CancellationToken.None);
        return (await models.GetAsync(id, CancellationToken.None))!;
    }

    private static PredictionPublication Publication() => new(100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, ["default-coefficients"], []);

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
