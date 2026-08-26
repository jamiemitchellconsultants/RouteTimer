using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Jobs;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;
using Testcontainers.PostgreSql;

namespace RouteTimer.Persistence.Tests;

public sealed class TrainingUploadRepositoryTests
{
    private static readonly DateTimeOffset UploadNow = new(2026, 8, 25, 14, 0, 0, TimeSpan.Zero);

    // Break caught: accepting an upload stores the FIT bytes and job in separate commits, so a job insert failure leaves an orphan upload.
    [Fact]
    public async Task Accepted_upload_commits_upload_and_parse_job_together_and_returns_both_ids()
    {
        await using var database = await StartDatabaseAsync();
        await using (var migrationContext = CreateContext(database))
        {
            await migrationContext.Database.MigrateAsync();
            await migrationContext.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE analysis_jobs
                ADD CONSTRAINT "CK_analysis_jobs_reject_parse_training_for_test"
                CHECK ("Type" <> 'ParseTraining')
                """);
        }

        var upload = new StoredUpload(Guid.NewGuid(), "ride.fit", "fit", [1, 2, 3], Enumerable.Repeat((byte)4, 32).ToArray(), UploadNow);
        await using (var failingContext = CreateContext(database))
        {
            var repository = new TrainingUploadRepository(failingContext);

            await Assert.ThrowsAsync<DbUpdateException>(() => repository.AcceptAsync(upload, UploadNow, null, CancellationToken.None));
        }

        await using (var assertionContext = CreateContext(database))
        {
            Assert.Empty(await assertionContext.Uploads.AsNoTracking().ToListAsync());
            Assert.Empty(await assertionContext.Jobs.AsNoTracking().ToListAsync());
            await assertionContext.Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE analysis_jobs
                DROP CONSTRAINT "CK_analysis_jobs_reject_parse_training_for_test"
                """);
        }

        await using var acceptingContext = CreateContext(database);
        var acceptingRepository = new TrainingUploadRepository(acceptingContext);
        var accepted = await acceptingRepository.AcceptAsync(upload, UploadNow, null, CancellationToken.None);

        Assert.Equal(TrainingUploadAcceptanceOutcome.Accepted, accepted.Outcome);
        Assert.Equal(upload.Id, accepted.UploadId);
        Assert.NotEqual(Guid.Empty, accepted.JobId);

        var savedUpload = await acceptingContext.Uploads.AsNoTracking().SingleAsync();
        Assert.Equal(upload.Id, savedUpload.Id);
        Assert.Equal(upload.Content, savedUpload.Content);
        Assert.Equal(upload.Sha256, savedUpload.Sha256);
        Assert.Equal(UploadNow, savedUpload.CreatedAt);

        var savedJob = await acceptingContext.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(accepted.JobId, savedJob.Id);
        Assert.Equal(JobType.ParseTraining.ToString(), savedJob.Type);
        Assert.Equal(upload.Id, savedJob.SubjectId);
        Assert.Equal(JobState.Queued.ToString(), savedJob.State);
        Assert.Equal(0, savedJob.ProgressPercent);
        Assert.Equal("queued", savedJob.ProgressStage);
        Assert.Equal(UploadNow, savedJob.CreatedAt);
        Assert.Equal(UploadNow, savedJob.UpdatedAt);
    }

    // Break caught: Garmin-ID or content-hash duplicates create a second upload/job or lose the original identifiers.
    [Fact]
    public async Task Accept_returns_existing_ids_for_a_Garmin_id_or_hash_duplicate_and_links_each_activity_once()
    {
        await using var database = await StartDatabaseAsync();
        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();
        var repository = new TrainingUploadRepository(context);

        var first = await repository.AcceptAsync(
            Upload("first.fit", 1), UploadNow,
            new GarminActivitySource("123", "Road ride"), CancellationToken.None);
        var sameId = await repository.AcceptAsync(
            Upload("renamed.fit", 2), UploadNow,
            new GarminActivitySource("123", "Road ride renamed"), CancellationToken.None);
        var sameHash = await repository.AcceptAsync(
            Upload("gravel.fit", 1), UploadNow,
            new GarminActivitySource("456", "Gravel ride"), CancellationToken.None);

        Assert.Equal(TrainingUploadAcceptanceOutcome.Accepted, first.Outcome);
        Assert.Equal(TrainingUploadAcceptanceOutcome.AlreadyImported, sameId.Outcome);
        Assert.Equal(TrainingUploadAcceptanceOutcome.DuplicateHash, sameHash.Outcome);
        Assert.Equal(first.UploadId, sameId.UploadId);
        Assert.Equal(first.UploadId, sameHash.UploadId);
        Assert.Equal(first.JobId, sameId.JobId);
        Assert.Equal(first.JobId, sameHash.JobId);
        Assert.Single(await context.Uploads.AsNoTracking().ToListAsync());
        Assert.Single(await context.Jobs.AsNoTracking().ToListAsync());
        Assert.Equal(2, await context.GarminActivityImports.AsNoTracking().CountAsync());
    }

    // Break caught: two PostgreSQL transactions can both pass a pre-insert Garmin-ID check and create duplicate evidence.
    [Fact]
    public async Task Concurrent_same_Garmin_id_creates_one_upload_job_and_link_with_idempotent_outcomes()
    {
        await using var database = await StartDatabaseAsync();
        await using (var migrationContext = CreateContext(database))
        {
            await migrationContext.Database.MigrateAsync();
        }

        await using var firstContext = CreateContext(database);
        await using var secondContext = CreateContext(database);
        var firstRepository = new TrainingUploadRepository(firstContext);
        var secondRepository = new TrainingUploadRepository(secondContext);

        var results = await Task.WhenAll(
            firstRepository.AcceptAsync(
                Upload("first.fit", 1), UploadNow,
                new GarminActivitySource("123", "Road ride"), CancellationToken.None),
            secondRepository.AcceptAsync(
                Upload("second.fit", 2), UploadNow,
                new GarminActivitySource("123", "Road ride renamed"), CancellationToken.None));

        Assert.Equal(
            [TrainingUploadAcceptanceOutcome.Accepted, TrainingUploadAcceptanceOutcome.AlreadyImported],
            results.Select(result => result.Outcome).Order().ToArray());
        Assert.Equal(results[0].UploadId, results[1].UploadId);
        Assert.Equal(results[0].JobId, results[1].JobId);

        await using var assertionContext = CreateContext(database);
        Assert.Single(await assertionContext.Uploads.AsNoTracking().ToListAsync());
        Assert.Single(await assertionContext.Jobs.AsNoTracking().Where(job => job.Type == JobType.ParseTraining.ToString()).ToListAsync());
        Assert.Single(await assertionContext.GarminActivityImports.AsNoTracking().ToListAsync());
    }

    private static StoredUpload Upload(string fileName, byte hashByte) =>
        new(Guid.NewGuid(), fileName, "fit", [hashByte], Enumerable.Repeat(hashByte, 32).ToArray(), UploadNow);

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
