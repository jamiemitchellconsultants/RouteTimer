using System.Security.Cryptography;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Training;

public enum UploadOutcome { Accepted, Duplicate, Invalid }
public sealed record TrainingUpload(string FileName, Stream Content);
public sealed record TrainingUploadResult(
    string FileName,
    UploadOutcome Outcome,
    Guid? UploadId,
    Guid? JobId,
    string? ErrorCode);

public sealed class TrainingUploadService
{
    private const int MaximumBytes = 50 * 1024 * 1024;
    private readonly ITrainingUploadRepository repository;
    private readonly TimeProvider timeProvider;

    public TrainingUploadService(ITrainingUploadRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<TrainingUploadResult>> AcceptAsync(IReadOnlyList<TrainingUpload> uploads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        var results = new List<TrainingUploadResult>(uploads.Count);
        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!upload.FileName.EndsWith(".fit", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new TrainingUploadResult(upload.FileName, UploadOutcome.Invalid, null, null, "invalid-fit-upload"));
                continue;
            }

            var content = await ReadBoundedAsync(upload.Content, cancellationToken);
            if (content is null || content.Length == 0)
            {
                results.Add(new TrainingUploadResult(upload.FileName, UploadOutcome.Invalid, null, null, "invalid-fit-upload"));
                continue;
            }

            var hash = SHA256.HashData(content);
            var uploadId = Guid.NewGuid();
            var now = timeProvider.GetUtcNow();
            var stored = await repository.AcceptAsync(
                new StoredUpload(uploadId, upload.FileName, "fit", content, hash, now),
                now,
                cancellationToken);
            results.Add(stored.Accepted
                ? new TrainingUploadResult(upload.FileName, UploadOutcome.Accepted, stored.UploadId, stored.JobId, null)
                : new TrainingUploadResult(upload.FileName, UploadOutcome.Duplicate, null, null, "duplicate-upload"));
        }

        return results;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var buffer = new byte[81920];
        await using var output = new MemoryStream();
        while (true)
        {
            var remaining = MaximumBytes - output.Length;
            if (remaining < 0)
            {
                return null;
            }

            var bytesToRead = (int)Math.Min(buffer.Length, remaining + 1);
            var read = await content.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }
    }
}
