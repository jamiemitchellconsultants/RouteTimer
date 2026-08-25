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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var inserted = context.Database.IsRelational()
            ? await InsertUploadIfAbsentAsync(upload, cancellationToken)
            : await InsertUploadIfAbsentInMemoryAsync(upload, cancellationToken);

        if (!inserted)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return new TrainingUploadAcceptance(false, null, null);
        }

        var jobId = Guid.NewGuid();
        context.Jobs.Add(new AnalysisJobEntity
        {
            Id = jobId,
            Type = JobType.ParseTraining.ToString(),
            SubjectId = upload.Id,
            State = JobState.Queued.ToString(),
            ProgressPercent = 0,
            ProgressStage = "queued",
            CreatedAt = now,
            UpdatedAt = now
        });

        await context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new TrainingUploadAcceptance(true, upload.Id, jobId);
    }

    private async Task<bool> InsertUploadIfAbsentAsync(StoredUpload upload, CancellationToken cancellationToken)
    {
        var insertedIds = await context.Database.SqlQuery<Guid>(
            $"""
            INSERT INTO stored_uploads ("Id", "Kind", "FileName", "Content", "Sha256", "CreatedAt")
            VALUES ({upload.Id}, {upload.Kind}, {upload.FileName}, {upload.Content}, {upload.Sha256}, {upload.CreatedAt})
            ON CONFLICT ("Kind", "Sha256") DO NOTHING
            RETURNING "Id" AS "Value"
            """).ToListAsync(cancellationToken);

        return insertedIds.Count == 1;
    }

    private async Task<bool> InsertUploadIfAbsentInMemoryAsync(StoredUpload upload, CancellationToken cancellationToken)
    {
        var exists = await context.Uploads.AnyAsync(
            entity => entity.Kind == upload.Kind && entity.Sha256.SequenceEqual(upload.Sha256),
            cancellationToken);
        if (exists)
        {
            return false;
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
        return true;
    }
}
