using RouteTimer.Services.Training;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Training;

public sealed class TrainingUploadServiceTests
{
    private static readonly DateTimeOffset UploadNow = new(2026, 8, 25, 15, 0, 0, TimeSpan.Zero);

    // Break caught: duplicate upload acceptance returns stale identifiers from a previous accepted upload.
    [Fact]
    public async Task Duplicate_upload_returns_duplicate_without_partial_identifiers()
    {
        var repository = new InMemoryTrainingUploadRepository();
        var service = new TrainingUploadService(repository, new FixedTimeProvider(UploadNow));
        var duplicateContent = new byte[] { 1, 2, 3 };

        var first = await service.AcceptAsync([new TrainingUpload("first.fit", new MemoryStream(duplicateContent))], CancellationToken.None);
        var duplicate = await service.AcceptAsync([new TrainingUpload("second.fit", new MemoryStream(duplicateContent))], CancellationToken.None);

        Assert.Equal(UploadOutcome.Accepted, Assert.Single(first).Outcome);
        var result = Assert.Single(duplicate);
        Assert.Equal("second.fit", result.FileName);
        Assert.Equal(UploadOutcome.Duplicate, result.Outcome);
        Assert.Null(result.UploadId);
        Assert.Null(result.JobId);
        Assert.Equal("duplicate-upload", result.ErrorCode);
        Assert.Single(repository.AcceptedUploads);
    }

    [Fact]
    public async Task Accept_batch_returns_independent_accepted_duplicate_and_invalid_results()
    {
        var repository = new InMemoryTrainingUploadRepository();
        var service = new TrainingUploadService(repository, new FixedTimeProvider(UploadNow));
        var uploads = new[]
        {
            new TrainingUpload("one.fit", new MemoryStream([1, 2, 3])),
            new TrainingUpload("copy.fit", new MemoryStream([1, 2, 3])),
            new TrainingUpload("broken.txt", new MemoryStream([9]))
        };

        var results = await service.AcceptAsync(uploads, CancellationToken.None);

        Assert.Collection(results,
            result => Assert.Equal(UploadOutcome.Accepted, result.Outcome),
            result => Assert.Equal(UploadOutcome.Duplicate, result.Outcome),
            result => Assert.Equal(UploadOutcome.Invalid, result.Outcome));
        var saved = Assert.Single(repository.AcceptedUploads);
        Assert.Equal("one.fit", saved.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.Content);
        Assert.Equal(UploadNow, saved.CreatedAt);
    }

    // Break caught: invalid FIT uploads are fully buffered or persisted before empty/oversized content is rejected.
    [Fact]
    public async Task Accept_rejects_empty_and_oversized_fit_uploads_without_calling_repository()
    {
        var repository = new InMemoryTrainingUploadRepository();
        var service = new TrainingUploadService(repository, new FixedTimeProvider(UploadNow));

        var results = await service.AcceptAsync(
            [
                new TrainingUpload("empty.fit", new MemoryStream()),
                new TrainingUpload("huge.fit", new OversizedStream(50 * 1024 * 1024 + 1))
            ],
            CancellationToken.None);

        Assert.Collection(results,
            result =>
            {
                Assert.Equal("empty.fit", result.FileName);
                Assert.Equal(UploadOutcome.Invalid, result.Outcome);
                Assert.Null(result.UploadId);
                Assert.Null(result.JobId);
                Assert.Equal("invalid-fit-upload", result.ErrorCode);
            },
            result =>
            {
                Assert.Equal("huge.fit", result.FileName);
                Assert.Equal(UploadOutcome.Invalid, result.Outcome);
                Assert.Null(result.UploadId);
                Assert.Null(result.JobId);
                Assert.Equal("invalid-fit-upload", result.ErrorCode);
            });
        Assert.Empty(repository.AcceptedUploads);
    }

    private sealed class InMemoryTrainingUploadRepository : ITrainingUploadRepository
    {
        public List<StoredUpload> AcceptedUploads { get; } = [];

        public Task<TrainingUploadAcceptance> AcceptAsync(StoredUpload upload, DateTimeOffset now, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AcceptedUploads.Any(existing => existing.Kind == upload.Kind && existing.Sha256.SequenceEqual(upload.Sha256)))
            {
                return Task.FromResult(new TrainingUploadAcceptance(false, null, null));
            }

            AcceptedUploads.Add(upload);
            return Task.FromResult(new TrainingUploadAcceptance(true, upload.Id, Guid.NewGuid()));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class OversizedStream(long length) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - position);
            if (read <= 0)
            {
                return 0;
            }

            Array.Fill(buffer, (byte)1, offset, read);
            position += read;
            return read;
        }
    }
}
