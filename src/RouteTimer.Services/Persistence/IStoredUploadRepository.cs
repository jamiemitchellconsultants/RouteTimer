namespace RouteTimer.Services.Persistence;

public sealed record StoredUpload(Guid Id, string FileName, string Kind, byte[] Content, byte[] Sha256, DateTimeOffset CreatedAt);

public interface IStoredUploadRepository
{
    Task<bool> StoreIfAbsentAsync(StoredUpload upload, CancellationToken cancellationToken);
}
