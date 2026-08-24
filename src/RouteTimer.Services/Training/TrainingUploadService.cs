using System.Security.Cryptography;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Training;

public enum UploadOutcome { Accepted, Duplicate, Invalid }
public sealed record TrainingUpload(string FileName, byte[] Content);
public sealed record TrainingUploadResult(string FileName, UploadOutcome Outcome, string? ErrorCode);

public sealed class TrainingUploadService
{
    private const int MaximumBytes = 50 * 1024 * 1024;
    private readonly IStoredUploadRepository repository;

    public TrainingUploadService(IStoredUploadRepository repository)
    {
        this.repository = repository;
    }

    public async Task<IReadOnlyList<TrainingUploadResult>> AcceptAsync(IReadOnlyList<TrainingUpload> uploads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        var results = new List<TrainingUploadResult>(uploads.Count);
        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!upload.FileName.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) || upload.Content.Length == 0 || upload.Content.Length > MaximumBytes)
            {
                results.Add(new TrainingUploadResult(upload.FileName, UploadOutcome.Invalid, "invalid-fit-upload"));
                continue;
            }

            var hash = SHA256.HashData(upload.Content);
            var stored = await repository.StoreIfAbsentAsync(
                new StoredUpload(upload.FileName, "fit", upload.Content, hash, DateTimeOffset.UtcNow),
                cancellationToken);
            results.Add(stored
                ? new TrainingUploadResult(upload.FileName, UploadOutcome.Accepted, null)
                : new TrainingUploadResult(upload.FileName, UploadOutcome.Duplicate, "duplicate-upload"));
        }

        return results;
    }
}
