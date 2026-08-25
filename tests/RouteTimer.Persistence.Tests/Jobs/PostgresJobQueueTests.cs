using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Jobs;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests.Jobs;

public sealed class PostgresJobQueueTests
{
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

    [Fact]
    public async Task Concurrent_claims_never_double_claim_a_job()
    {
        await using var database = await StartDatabaseAsync();
        await using (var migrationContext = CreateContext(database))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var now = DateTimeOffset.UtcNow;
        var jobIds = new List<Guid>();
        await using (var seedContext = CreateContext(database))
        {
            var seedQueue = new PostgresJobQueue(seedContext);
            for (var i = 0; i < 5; i++)
            {
                jobIds.Add(await seedQueue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None));
            }
        }

        var claimTasks = new List<Task<AnalysisJob?>>();
        var contexts = new List<RouteTimerDbContext>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var workerContext = CreateContext(database);
                contexts.Add(workerContext);
                var workerId = $"worker-{i}";
                var queue = new PostgresJobQueue(workerContext);
                claimTasks.Add(queue.ClaimAsync(workerId, now, TimeSpan.FromMinutes(5), CancellationToken.None));
            }

            var results = await Task.WhenAll(claimTasks);

            Assert.All(results, result => Assert.NotNull(result));
            var claimedIds = results.Select(result => result!.Id).ToList();
            Assert.Equal(5, claimedIds.Distinct().Count());
            Assert.Equal(jobIds.OrderBy(id => id), claimedIds.OrderBy(id => id));
        }
        finally
        {
            foreach (var workerContext in contexts)
            {
                await workerContext.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Expired_running_job_can_be_claimed_by_a_different_worker_and_attempt_count_increments()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        var first = await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        var second = await queue.ClaimAsync("worker-b", now.AddMinutes(3), TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(id, first!.Id);
        Assert.Equal(id, second!.Id);
        Assert.Equal("worker-b", second.WorkerId);
        Assert.Equal(2, second.AttemptCount);
    }

    [Fact]
    public async Task RenewLeaseAsync_extends_lease_when_called_by_owning_worker_on_running_job()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);

        var renewed = now.AddMinutes(1);
        var result = await queue.RenewLeaseAsync(id, "worker-a", renewed, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.True(result);
        var reloaded = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == id);
        Assert.Equal(renewed.AddMinutes(10), reloaded.LeaseExpiresAt);
    }

    [Fact]
    public async Task RenewLeaseAsync_returns_false_when_called_by_a_different_worker()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);

        var result = await queue.RenewLeaseAsync(id, "worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task RenewLeaseAsync_returns_false_when_job_is_not_running()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);

        var result = await queue.RenewLeaseAsync(id, "worker-a", now, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task CompleteAsync_transitions_a_running_job_to_succeeded()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);

        var result = await queue.CompleteAsync(id, "worker-a", CancellationToken.None);

        Assert.True(result);
        var reloaded = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == id);
        Assert.Equal(JobState.Succeeded.ToString(), reloaded.State);
        Assert.Null(reloaded.WorkerId);
        Assert.Null(reloaded.LeaseExpiresAt);
    }

    [Fact]
    public async Task CompleteAsync_returns_false_and_leaves_the_job_untouched_when_called_by_a_non_owning_worker()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);

        var result = await queue.CompleteAsync(id, "worker-b", CancellationToken.None);

        Assert.False(result);
        var reloaded = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == id);
        Assert.Equal(JobState.Running.ToString(), reloaded.State);
        Assert.Equal("worker-a", reloaded.WorkerId);
    }

    [Fact]
    public async Task FailAsync_permanent_transitions_to_failed_and_persists_diagnostic()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);

        var result = await queue.FailAsync(id, "worker-a", permanent: true, diagnosticCode: "invalid_fit", diagnosticMessage: "The FIT file could not be decoded.", CancellationToken.None);

        Assert.True(result);
        var reloaded = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == id);
        Assert.Equal(JobState.Failed.ToString(), reloaded.State);
        Assert.Equal("invalid_fit", reloaded.DiagnosticCode);
        Assert.Equal("The FIT file could not be decoded.", reloaded.DiagnosticMessage);
        Assert.Null(reloaded.WorkerId);
        Assert.Null(reloaded.LeaseExpiresAt);
    }

    [Fact]
    public async Task FailAsync_returns_false_and_leaves_the_job_untouched_when_called_by_a_non_owning_worker()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);

        var result = await queue.FailAsync(id, "worker-b", permanent: false, diagnosticCode: "timeout", diagnosticMessage: "Stale attempt.", CancellationToken.None);

        Assert.False(result);
        var reloaded = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == id);
        Assert.Equal(JobState.Running.ToString(), reloaded.State);
        Assert.Equal("worker-a", reloaded.WorkerId);
        Assert.Null(reloaded.DiagnosticCode);
    }

    [Fact]
    public async Task FailAsync_transient_with_attempts_remaining_returns_the_job_to_queued_and_it_becomes_claimable()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);

        var result = await queue.FailAsync(id, "worker-a", permanent: false, diagnosticCode: "timeout", diagnosticMessage: "Transient failure.", CancellationToken.None);

        Assert.True(result);
        var reloaded = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == id);
        Assert.Equal(JobState.Queued.ToString(), reloaded.State);
        Assert.Null(reloaded.WorkerId);
        Assert.Null(reloaded.LeaseExpiresAt);
        Assert.Equal("timeout", reloaded.DiagnosticCode);

        var reclaimed = await queue.ClaimAsync("worker-b", now.AddMinutes(1), TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(reclaimed);
        Assert.Equal(id, reclaimed!.Id);
        Assert.Equal(2, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task FailAsync_transient_at_third_attempt_transitions_to_failed_instead_of_retrying()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var workerId = $"worker-{attempt}";
            var claimed = await queue.ClaimAsync(workerId, now.AddMinutes(attempt), TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(attempt, claimed!.AttemptCount);
            if (attempt < 3)
            {
                var result = await queue.FailAsync(id, workerId, permanent: false, diagnosticCode: "timeout", diagnosticMessage: "Transient failure.", CancellationToken.None);
                Assert.True(result);
            }
        }

        var finalResult = await queue.FailAsync(id, "worker-3", permanent: false, diagnosticCode: "timeout", diagnosticMessage: "Out of attempts.", CancellationToken.None);

        Assert.True(finalResult);
        var reloaded = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == id);
        Assert.Equal(JobState.Failed.ToString(), reloaded.State);
        Assert.Equal(3, reloaded.AttemptCount);
    }

    [Fact]
    public async Task FailAsync_terminal_predict_route_failure_updates_prediction_and_job_together()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var model = await SaveModelAsync(context);
        var submission = await new PredictionRepository(context).CreateQueuedAsync(Creation(model), CancellationToken.None);
        var queue = new PostgresJobQueue(context);
        var now = DateTimeOffset.UtcNow;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.NotNull(await queue.ClaimAsync($"worker-{attempt}", now.AddMinutes(attempt), TimeSpan.FromMinutes(2), CancellationToken.None));
            Assert.True(await queue.FailAsync(submission.JobId, $"worker-{attempt}", permanent: false, "processing-error", "Transient failure.", CancellationToken.None));
            var queued = await context.Predictions.AsNoTracking().SingleAsync(entity => entity.Id == submission.PredictionId);
            Assert.Equal(PredictionState.Queued.ToString(), queued.State);
        }

        Assert.NotNull(await queue.ClaimAsync("worker-3", now.AddMinutes(3), TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.True(await queue.FailAsync(submission.JobId, "worker-3", permanent: false, "processing-error", "Terminal failure.", CancellationToken.None));

        var job = await context.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == submission.JobId);
        var prediction = await context.Predictions.AsNoTracking().SingleAsync(entity => entity.Id == submission.PredictionId);
        Assert.Equal(JobState.Failed.ToString(), job.State);
        Assert.Equal(PredictionState.Failed.ToString(), prediction.State);
        Assert.Equal(new[] { "processing-error: Terminal failure." }, prediction.Warnings);
        Assert.NotNull(prediction.CompletedAt);
        Assert.Null(job.WorkerId);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task FailAsync_rolls_back_job_when_terminal_prediction_update_fails()
    {
        await using var database = await StartDatabaseAsync();
        Guid jobId;
        Guid predictionId;
        await using (var setup = CreateContext(database))
        {
            await setup.Database.MigrateAsync();
            var model = await SaveModelAsync(setup);
            var submission = await new PredictionRepository(setup).CreateQueuedAsync(Creation(model), CancellationToken.None);
            jobId = submission.JobId;
            predictionId = submission.PredictionId;
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_prediction_failure() RETURNS trigger AS $$
                BEGIN
                    IF NEW."State" = 'Failed' THEN RAISE EXCEPTION 'forced-prediction-failure'; END IF;
                    RETURN NEW;
                END $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_prediction_failure BEFORE UPDATE ON predictions FOR EACH ROW EXECUTE FUNCTION reject_prediction_failure();
                """);
        }

        await using (var failingContext = CreateContext(database))
        {
            var queue = new PostgresJobQueue(failingContext);
            Assert.NotNull(await queue.ClaimAsync("worker-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), CancellationToken.None));
            await Assert.ThrowsAsync<DbUpdateException>(() => queue.FailAsync(jobId, "worker-a", permanent: true, "invalid-route", "Permanent failure.", CancellationToken.None));
        }

        await using var verify = CreateContext(database);
        var job = await verify.Jobs.AsNoTracking().SingleAsync(entity => entity.Id == jobId);
        var prediction = await verify.Predictions.AsNoTracking().SingleAsync(entity => entity.Id == predictionId);
        Assert.Equal(JobState.Running.ToString(), job.State);
        Assert.Equal("worker-a", job.WorkerId);
        Assert.Equal(PredictionState.Queued.ToString(), prediction.State);
    }

    [Fact]
    public async Task Concurrent_EnqueueIfNotPendingAsync_calls_for_the_same_subject_coalesce_to_a_single_job()
    {
        await using var database = await StartDatabaseAsync();
        await using (var migrationContext = CreateContext(database))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var subjectId = ModelSubject.Id;
        var enqueueTasks = new List<Task<Guid>>();
        var contexts = new List<RouteTimerDbContext>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var workerContext = CreateContext(database);
                contexts.Add(workerContext);
                var queue = new PostgresJobQueue(workerContext);
                enqueueTasks.Add(queue.EnqueueIfNotPendingAsync(JobType.BuildModel, subjectId, CancellationToken.None));
            }

            var results = await Task.WhenAll(enqueueTasks);

            Assert.Single(results.Distinct());

            await using var verifyContext = CreateContext(database);
            var jobs = await verifyContext.Jobs
                .Where(job => job.Type == JobType.BuildModel.ToString() && job.SubjectId == subjectId)
                .ToListAsync();
            var onlyJob = Assert.Single(jobs);
            Assert.All(results, result => Assert.Equal(onlyJob.Id, result));
        }
        finally
        {
            foreach (var workerContext in contexts)
            {
                await workerContext.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task EnqueueIfNotPendingAsync_inserts_a_fresh_job_once_the_prior_one_reaches_a_terminal_state()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var subjectId = ModelSubject.Id;
        var now = DateTimeOffset.UtcNow;

        var firstId = await queue.EnqueueIfNotPendingAsync(JobType.BuildModel, subjectId, CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        await queue.CompleteAsync(firstId, "worker-a", CancellationToken.None);

        var secondId = await queue.EnqueueIfNotPendingAsync(JobType.BuildModel, subjectId, CancellationToken.None);

        Assert.NotEqual(firstId, secondId);
        var jobs = await context.Jobs.AsNoTracking().Where(job => job.SubjectId == subjectId).ToListAsync();
        Assert.Equal(2, jobs.Count);
    }

    [Fact]
    public async Task EnqueueIfNotPendingAsync_still_coalesces_to_the_same_job_after_a_transient_failure_returns_it_to_queued()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var queue = new PostgresJobQueue(context);
        var subjectId = ModelSubject.Id;
        var now = DateTimeOffset.UtcNow;

        var firstId = await queue.EnqueueIfNotPendingAsync(JobType.BuildModel, subjectId, CancellationToken.None);
        await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        // Transient (non-permanent) failure with attempts remaining returns the job to Queued rather
        // than Failed - the unique index still guards it, so a later caller must still coalesce onto
        // this same row instead of inserting a duplicate.
        await queue.FailAsync(firstId, "worker-a", permanent: false, diagnosticCode: "timeout", diagnosticMessage: "Transient failure.", CancellationToken.None);

        var secondId = await queue.EnqueueIfNotPendingAsync(JobType.BuildModel, subjectId, CancellationToken.None);

        Assert.Equal(firstId, secondId);
        var jobs = await context.Jobs.AsNoTracking().Where(job => job.SubjectId == subjectId).ToListAsync();
        var onlyJob = Assert.Single(jobs);
        Assert.Equal(JobState.Queued.ToString(), onlyJob.State);
    }

    /// <summary>
    /// Reproduces, deterministically, the narrow race the code guards against: a racing insert fails
    /// with a unique-index conflict against a job that is still Queued at that exact instant, but that
    /// queued row becomes terminal (by a different connection) before the fallback lookup for "who won"
    /// runs - so that lookup finds nothing. EnqueueIfNotPendingAsync must retry its insert in that case
    /// rather than letting an InvalidOperationException from an empty sequence escape (which would
    /// otherwise surface as a spurious failure of an unrelated, already-successful caller, e.g.
    /// ParseTrainingJobHandler after it already saved a parsed activity).
    ///
    /// Postgres only checks the unique constraint against rows committed at insert time, so genuinely
    /// reproducing "conflict, then the row leaves Queued before the next statement" requires the
    /// terminal transition to happen inside the single await gap between the racing insert's failure and
    /// its own fallback SELECT - not just "at some point during the test". A SaveChangesInterceptor
    /// gives us that precise hook: EF Core invokes SaveChangesFailedAsync synchronously, while still
    /// inside the failing SaveChangesAsync call, before the exception is handed back to our code's catch
    /// block.
    /// </summary>
    [Fact]
    public async Task EnqueueIfNotPendingAsync_retries_once_when_the_conflicting_job_becomes_terminal_before_the_fallback_lookup_runs()
    {
        await using var database = await StartDatabaseAsync();
        await using (var migrationContext = CreateContext(database))
        {
            await migrationContext.Database.MigrateAsync();
        }

        var subjectId = ModelSubject.Id;
        await using var completerContext = CreateContext(database);
        var completerQueue = new PostgresJobQueue(completerContext);
        var conflictingId = await completerQueue.EnqueueAsync(JobType.BuildModel, subjectId, CancellationToken.None);

        // Fires from inside the racing context's own failing SaveChangesAsync call - i.e. strictly
        // between its failed insert and its fallback SELECT - and moves the conflicting queued job to a
        // terminal state there, via a wholly separate connection, so the fallback SELECT is guaranteed
        // to find nothing still queued.
        var interceptor = new CompleteJobOnSaveFailureInterceptor(
            () => completerContext.Jobs
                .Where(job => job.Id == conflictingId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.State, JobState.Succeeded.ToString())
                    .SetProperty(job => job.ProgressPercent, 100)
                    .SetProperty(job => job.ProgressStage, "completed")
                    .SetProperty(job => job.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(job => job.CompletedAt, DateTimeOffset.UtcNow)));
        var racingOptions = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseNpgsql(database.GetConnectionString())
            .AddInterceptors(interceptor)
            .Options;
        await using var racingContext = new RouteTimerDbContext(racingOptions);
        var racingQueue = new PostgresJobQueue(racingContext);

        var resultId = await racingQueue.EnqueueIfNotPendingAsync(JobType.BuildModel, subjectId, CancellationToken.None);

        Assert.True(interceptor.WasInvoked);
        Assert.NotEqual(conflictingId, resultId);
        var jobs = await completerContext.Jobs.AsNoTracking().Where(job => job.SubjectId == subjectId).ToListAsync();
        Assert.Equal(2, jobs.Count);
        var freshJob = Assert.Single(jobs, job => job.Id == resultId);
        Assert.Equal(JobState.Queued.ToString(), freshJob.State);
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
        var id = await models.SaveAsync(new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1"), profile,
            new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08), CancellationToken.None);
        return (await models.GetAsync(id, CancellationToken.None))!;
    }

    /// <summary>Completes a specific job, via a caller-supplied callback, the moment the context this
    /// interceptor is attached to fails a SaveChangesAsync call - used to land a completion inside the
    /// otherwise-unreachable gap between a failed racing insert and its own fallback lookup.</summary>
    private sealed class CompleteJobOnSaveFailureInterceptor(Func<Task> onSaveFailed) : SaveChangesInterceptor
    {
        public bool WasInvoked { get; private set; }

        public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            await onSaveFailed();
            await base.SaveChangesFailedAsync(eventData, cancellationToken);
        }
    }
}
