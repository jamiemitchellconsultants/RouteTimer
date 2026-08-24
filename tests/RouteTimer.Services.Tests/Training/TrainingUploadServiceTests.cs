using RouteTimer.Services.Training;
using RouteTimer.Services.Persistence;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;

namespace RouteTimer.Services.Tests.Training;

public sealed class TrainingUploadServiceTests
{
    [Fact]
    public async Task Accept_batch_returns_independent_accepted_duplicate_and_invalid_results()
    {
        var repository = new InMemoryStoredUploadRepository();
        var jobs = new InMemoryJobQueue();
        var service = new TrainingUploadService(repository, jobs);
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
        Assert.Single(jobs.Enqueued);
    }

    private sealed class InMemoryJobQueue : IJobQueue
    {
        public List<(JobType Type, Guid SubjectId)> Enqueued { get; } = [];
        public Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken) { Enqueued.Add((type, subjectId)); return Task.FromResult(Guid.NewGuid()); }
        public Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult<AnalysisJob?>(null);
        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task CompleteAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailAsync(Guid jobId, bool permanent, string? diagnosticCode, string? diagnosticMessage, CancellationToken cancellationToken) => Task.CompletedTask;
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
