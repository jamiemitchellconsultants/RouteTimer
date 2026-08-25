using RouteTimer.Services.Training;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Training;

public sealed class TrainingUploadServiceTests
{
    private static readonly DateTimeOffset UploadNow = new(2026, 8, 25, 15, 0, 0, TimeSpan.Zero);

    // Break caught: duplicate upload acceptance discards the identifiers needed by Garmin import results.
    [Fact]
    public async Task Duplicate_upload_returns_duplicate_with_the_existing_upload_and_job_identifiers()
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
        Assert.Equal(first[0].UploadId, result.UploadId);
        Assert.Equal(first[0].JobId, result.JobId);
        Assert.Equal("duplicate-upload", result.ErrorCode);
        Assert.Single(repository.AcceptedUploads);
    }

    // Break caught: Garmin provenance is dropped before the repository can enforce activity-ID idempotency.
    [Fact]
    public async Task Garmin_upload_forwards_source_and_returns_existing_ids_for_activity_id_duplicates()
    {
        var repository = new InMemoryTrainingUploadRepository();
        var service = new TrainingUploadService(repository, new FixedTimeProvider(UploadNow));

        var first = Assert.Single(await service.AcceptAsync(
            [new TrainingUpload("first.fit", new MemoryStream([1, 2, 3]), new GarminActivitySource("123", "Road ride"))],
            CancellationToken.None));
        var duplicate = Assert.Single(await service.AcceptAsync(
            [new TrainingUpload("renamed.fit", new MemoryStream([4, 5, 6]), new GarminActivitySource("123", "Road ride renamed"))],
            CancellationToken.None));

        Assert.Equal(UploadOutcome.Accepted, first.Outcome);
        Assert.Equal(TrainingUploadAcceptanceOutcome.Accepted, first.AcceptanceOutcome);
        Assert.Equal(UploadOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(TrainingUploadAcceptanceOutcome.AlreadyImported, duplicate.AcceptanceOutcome);
        Assert.Equal(first.UploadId, duplicate.UploadId);
        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.Equal("duplicate-upload", duplicate.ErrorCode);
        Assert.Equal(new GarminActivitySource("123", "Road ride"), Assert.Single(repository.GarminSources));
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
        public List<GarminActivitySource> GarminSources { get; } = [];
        private readonly Dictionary<Guid, Guid> jobs = [];

        public Task<TrainingUploadAcceptance> AcceptAsync(
            StoredUpload upload,
            DateTimeOffset now,
            GarminActivitySource? garminSource,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (garminSource is not null)
            {
                var sourceIndex = GarminSources.FindIndex(source => source.ActivityId == garminSource.ActivityId);
                if (sourceIndex >= 0)
                {
                    var existingUpload = AcceptedUploads[sourceIndex];
                    return Task.FromResult(new TrainingUploadAcceptance(
                        TrainingUploadAcceptanceOutcome.AlreadyImported,
                        existingUpload.Id,
                        jobs[existingUpload.Id]));
                }
            }

            var duplicate = AcceptedUploads.FirstOrDefault(
                existing => existing.Kind == upload.Kind && existing.Sha256.SequenceEqual(upload.Sha256));
            if (duplicate is not null)
            {
                if (garminSource is not null)
                {
                    GarminSources.Add(garminSource);
                }

                return Task.FromResult(new TrainingUploadAcceptance(
                    TrainingUploadAcceptanceOutcome.DuplicateHash,
                    duplicate.Id,
                    jobs[duplicate.Id]));
            }

            AcceptedUploads.Add(upload);
            if (garminSource is not null)
            {
                GarminSources.Add(garminSource);
            }

            var jobId = Guid.NewGuid();
            jobs.Add(upload.Id, jobId);
            return Task.FromResult(new TrainingUploadAcceptance(
                TrainingUploadAcceptanceOutcome.Accepted,
                upload.Id,
                jobId));
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
