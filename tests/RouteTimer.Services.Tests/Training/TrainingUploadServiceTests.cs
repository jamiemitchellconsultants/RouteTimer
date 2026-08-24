using RouteTimer.Services.Training;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Training;

public sealed class TrainingUploadServiceTests
{
    [Fact]
    public async Task Accept_batch_returns_independent_accepted_duplicate_and_invalid_results()
    {
        var repository = new InMemoryStoredUploadRepository();
        var service = new TrainingUploadService(repository);
        var uploads = new[]
        {
            new TrainingUpload("one.fit", [1, 2, 3]),
            new TrainingUpload("copy.fit", [1, 2, 3]),
            new TrainingUpload("broken.txt", [9])
        };

        var results = await service.AcceptAsync(uploads, CancellationToken.None);

        Assert.Collection(results,
            result => Assert.Equal(UploadOutcome.Accepted, result.Outcome),
            result => Assert.Equal(UploadOutcome.Duplicate, result.Outcome),
            result => Assert.Equal(UploadOutcome.Invalid, result.Outcome));
        var saved = Assert.Single(repository.Uploads);
        Assert.Equal("one.fit", saved.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.Content);
    }

    private sealed class InMemoryStoredUploadRepository : IStoredUploadRepository
    {
        public List<StoredUpload> Uploads { get; } = [];

        public Task<bool> StoreIfAbsentAsync(StoredUpload upload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Uploads.Any(existing => existing.Kind == upload.Kind && existing.Sha256.SequenceEqual(upload.Sha256)))
            {
                return Task.FromResult(false);
            }

            Uploads.Add(upload);
            return Task.FromResult(true);
        }
    }
}
