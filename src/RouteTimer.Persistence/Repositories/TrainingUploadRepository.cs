using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Jobs;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class TrainingUploadRepository(RouteTimerDbContext context) : ITrainingUploadRepository
{
    public async Task<TrainingUploadAcceptance> AcceptAsync(
        StoredUpload upload,
        DateTimeOffset now,
        GarminActivitySource? garminSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (garminSource is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(garminSource.ActivityId);
            ArgumentException.ThrowIfNullOrWhiteSpace(garminSource.ActivityName);

            if (context.Database.IsRelational())
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtext({garminSource.ActivityId}))",
                    cancellationToken);
            }

            var linkedUploadId = await context.GarminActivityImports
                .AsNoTracking()
                .Where(import => import.GarminActivityId == garminSource.ActivityId)
                .Select(import => (Guid?)import.UploadId)
                .SingleOrDefaultAsync(cancellationToken);
            if (linkedUploadId is not null)
            {
                var linkedJobId = await FindParseJobIdAsync(linkedUploadId.Value, cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return new TrainingUploadAcceptance(
                    TrainingUploadAcceptanceOutcome.AlreadyImported,
                    linkedUploadId.Value,
                    linkedJobId);
            }
        }

        var insertedUploadId = context.Database.IsRelational()
            ? await InsertUploadIfAbsentAsync(upload, cancellationToken)
            : await InsertUploadIfAbsentInMemoryAsync(upload, cancellationToken);
        var uploadId = insertedUploadId ?? await FindExistingUploadIdAsync(upload, cancellationToken);
        Guid jobId;
        var outcome = TrainingUploadAcceptanceOutcome.DuplicateHash;
        if (insertedUploadId is not null)
        {
            outcome = TrainingUploadAcceptanceOutcome.Accepted;
            jobId = Guid.NewGuid();
            context.Jobs.Add(new AnalysisJobEntity
            {
                Id = jobId,
                Type = JobType.ParseTraining.ToString(),
                SubjectId = uploadId,
                State = JobState.Queued.ToString(),
                ProgressPercent = 0,
                ProgressStage = "queued",
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            jobId = await FindParseJobIdAsync(uploadId, cancellationToken);
        }

        if (garminSource is not null)
        {
            context.GarminActivityImports.Add(new GarminActivityImportEntity
            {
                GarminActivityId = garminSource.ActivityId,
                UploadId = uploadId,
                ActivityName = garminSource.ActivityName,
                LinkedAt = now
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new TrainingUploadAcceptance(outcome, uploadId, jobId);
    }

    private async Task<Guid?> InsertUploadIfAbsentAsync(StoredUpload upload, CancellationToken cancellationToken)
    {
        var insertedIds = await context.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO stored_uploads ("Id", "Kind", "FileName", "Content", "Sha256", "CreatedAt")
            VALUES ({upload.Id}, {upload.Kind}, {upload.FileName}, {upload.Content}, {upload.Sha256}, {upload.CreatedAt})
            ON CONFLICT ("Kind", "Sha256") DO NOTHING
            RETURNING "Id" AS "Value"
            """).ToListAsync(cancellationToken);

        var insertedId = insertedIds.SingleOrDefault();
        return insertedId == Guid.Empty ? null : insertedId;
    }

    private async Task<Guid?> InsertUploadIfAbsentInMemoryAsync(StoredUpload upload, CancellationToken cancellationToken)
    {
        var exists = await context.Uploads.AnyAsync(
            entity => entity.Kind == upload.Kind && entity.Sha256.SequenceEqual(upload.Sha256),
            cancellationToken);
        if (exists)
        {
            return null;
        }

        context.Uploads.Add(new StoredUploadEntity
        {
            Id = upload.Id,
            Kind = upload.Kind,
            FileName = upload.FileName,
            Content = upload.Content,
            Sha256 = upload.Sha256,
            CreatedAt = upload.CreatedAt
        });
        return upload.Id;
    }

    private async Task<Guid> FindExistingUploadIdAsync(
        StoredUpload upload,
        CancellationToken cancellationToken)
    {
        if (context.Database.IsRelational())
        {
            return await context.Database.SqlQuery<Guid>(
                $"""
                SELECT "Id" AS "Value"
                FROM stored_uploads
                WHERE "Kind" = {upload.Kind} AND "Sha256" = {upload.Sha256}
                """).SingleAsync(cancellationToken);
        }

        return await context.Uploads
            .Where(entity => entity.Kind == upload.Kind && entity.Sha256.SequenceEqual(upload.Sha256))
            .Select(entity => entity.Id)
            .SingleAsync(cancellationToken);
    }

    private Task<Guid> FindParseJobIdAsync(Guid uploadId, CancellationToken cancellationToken) =>
        context.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.ParseTraining.ToString() && job.SubjectId == uploadId)
            .Select(job => job.Id)
            .SingleAsync(cancellationToken);
}
