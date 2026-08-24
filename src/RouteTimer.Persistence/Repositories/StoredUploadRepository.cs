using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class StoredUploadRepository(RouteTimerDbContext context) : IStoredUploadRepository
{
    public async Task<bool> StoreIfAbsentAsync(StoredUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var exists = await context.Uploads.AnyAsync(
            entity => entity.Kind == upload.Kind && entity.Sha256 == upload.Sha256,
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
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<StoredUpload?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await context.Uploads.SingleOrDefaultAsync(upload => upload.Id == id, cancellationToken);
        return entity is null
            ? null
            : new StoredUpload(entity.Id, entity.FileName, entity.Kind, entity.Content, entity.Sha256, entity.CreatedAt);
    }
}
