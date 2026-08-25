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

            await Assert.ThrowsAsync<DbUpdateException>(() => repository.AcceptAsync(upload, UploadNow, CancellationToken.None));
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
        var accepted = await acceptingRepository.AcceptAsync(upload, UploadNow, CancellationToken.None);

        Assert.True(accepted.Accepted);
        Assert.Equal(upload.Id, accepted.UploadId);
        Assert.NotNull(accepted.JobId);

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
