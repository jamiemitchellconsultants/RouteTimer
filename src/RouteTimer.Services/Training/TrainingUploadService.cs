using System.Security.Cryptography;

namespace RouteTimer.Services.Training;

public enum UploadOutcome { Accepted, Duplicate, Invalid }
public sealed record TrainingUpload(string FileName, byte[] Content);
public sealed record TrainingUploadResult(string FileName, UploadOutcome Outcome, string? ErrorCode);

public sealed class TrainingUploadService
{
    private const int MaximumBytes = 50 * 1024 * 1024;
    private readonly HashSet<string> knownHashes = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<TrainingUploadResult>> AcceptAsync(IReadOnlyList<TrainingUpload> uploads, CancellationToken cancellationToken)
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

            var hash = Convert.ToHexString(SHA256.HashData(upload.Content));
            results.Add(knownHashes.Add(hash)
                ? new TrainingUploadResult(upload.FileName, UploadOutcome.Accepted, null)
                : new TrainingUploadResult(upload.FileName, UploadOutcome.Duplicate, "duplicate-upload"));
        }

        return Task.FromResult<IReadOnlyList<TrainingUploadResult>>(results);
    }
}
