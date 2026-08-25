using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Tests.Activities;
using RouteTimer.Services.Training;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Training;

public sealed class ParseTrainingJobHandlerTests
{
    private static readonly DateTimeOffset SampleStartedAt = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SampleEndedAt = new(2026, 8, 1, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SampleCleanedEndedAt = new(2026, 8, 1, 9, 25, 0, TimeSpan.Zero);

    private static readonly ParsedFitActivity SampleParsed = new(
        "Morning Ride",
        ActivitySport.Cycling,
        SampleStartedAt,
        SampleEndedAt,
        "Garmin",
        "Edge",
        [],
        TimeSpan.FromMinutes(30),
        10_000,
        250);

    private static readonly CleanedActivity SampleCleaned = new(
        "Morning Ride",
        [],
        TimeSpan.FromMinutes(25),
        new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []),
        ActivityFixtures.Metadata("ride.fit", SampleStartedAt, SampleCleanedEndedAt, "Garmin", "Edge", 10_000, 250));

    [Fact]
    public async Task Handle_parses_cleans_and_saves_the_upload_referenced_by_the_job()
    {
        var uploadId = Guid.NewGuid();
        var uploads = new FakeStoredUploadRepository { Upload = new StoredUpload(uploadId, "ride.fit", "fit", [1, 2, 3], [], DateTimeOffset.UtcNow) };
        var parser = new FakeFitActivityParser { Result = SampleParsed };
        var cleaner = new FakeTrainingCleaner { Result = SampleCleaned };
        var activities = new FakeTrainingActivityRepository();
        var jobs = new FakeJobQueue();
        var handler = new ParseTrainingJobHandler(uploads, parser, cleaner, activities, jobs);
        var job = RunningJob(uploadId);

        await handler.HandleAsync(job, CancellationToken.None);

        Assert.Same(SampleParsed, cleaner.ReceivedActivity);
        Assert.Equal("ride.fit", cleaner.ReceivedSourceFileName);
        Assert.Equal(uploadId, activities.SavedUploadId);
        Assert.Same(SampleCleaned, activities.SavedActivity);
    }

    [Fact]
    public async Task Handle_enqueues_a_coalesced_build_model_job_after_a_successful_save()
    {
        var uploadId = Guid.NewGuid();
        var uploads = new FakeStoredUploadRepository { Upload = new StoredUpload(uploadId, "ride.fit", "fit", [1, 2, 3], [], DateTimeOffset.UtcNow) };
        var parser = new FakeFitActivityParser { Result = SampleParsed };
        var cleaner = new FakeTrainingCleaner { Result = SampleCleaned };
        var activities = new FakeTrainingActivityRepository();
        var jobs = new FakeJobQueue();
        var handler = new ParseTrainingJobHandler(uploads, parser, cleaner, activities, jobs);
        var job = RunningJob(uploadId);

        await handler.HandleAsync(job, CancellationToken.None);

        var enqueued = Assert.Single(jobs.EnqueuedIfNotPending);
        Assert.Equal(JobType.BuildModel, enqueued.Type);
        Assert.Equal(ModelSubject.Id, enqueued.SubjectId);
    }

    [Fact]
    public async Task Handle_throws_permanent_activity_input_exception_when_upload_is_missing()
    {
        var uploads = new FakeStoredUploadRepository { Upload = null };
        var handler = new ParseTrainingJobHandler(uploads, new FakeFitActivityParser(), new FakeTrainingCleaner(), new FakeTrainingActivityRepository(), new FakeJobQueue());
        var job = RunningJob(Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<ActivityInputException>(() => handler.HandleAsync(job, CancellationToken.None));

        Assert.Equal("upload-missing", exception.Code);
    }

    [Fact]
    public async Task Handle_lets_parser_failures_propagate_unchanged()
    {
        var uploadId = Guid.NewGuid();
        var uploads = new FakeStoredUploadRepository { Upload = new StoredUpload(uploadId, "ride.fit", "fit", [1, 2, 3], [], DateTimeOffset.UtcNow) };
        var parser = new FakeFitActivityParser { ThrownException = new ActivityInputException("corrupt-fit", "The FIT file is corrupt.") };
        var handler = new ParseTrainingJobHandler(uploads, parser, new FakeTrainingCleaner(), new FakeTrainingActivityRepository(), new FakeJobQueue());
        var job = RunningJob(uploadId);

        var exception = await Assert.ThrowsAsync<ActivityInputException>(() => handler.HandleAsync(job, CancellationToken.None));

        Assert.Equal("corrupt-fit", exception.Code);
        Assert.Equal("The FIT file is corrupt.", exception.Message);
    }

    private sealed class FakeStoredUploadRepository : IStoredUploadRepository
    {
        public StoredUpload? Upload { get; init; }

        public Task<bool> StoreIfAbsentAsync(StoredUpload upload, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoredUpload?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Upload);
    }

    private sealed class FakeFitActivityParser : IFitActivityParser
    {
        public ParsedFitActivity? Result { get; init; }
        public ActivityInputException? ThrownException { get; init; }

        public Task<ParsedFitActivity> ParseAsync(Stream input, CancellationToken cancellationToken)
        {
            if (ThrownException is not null)
            {
                throw ThrownException;
            }

            return Task.FromResult(Result!);
        }
    }

    private sealed class FakeTrainingCleaner : ITrainingCleaner
    {
        public CleanedActivity? Result { get; init; }
        public ParsedFitActivity? ReceivedActivity { get; private set; }
        public string? ReceivedSourceFileName { get; private set; }

        public CleanedActivity Clean(ParsedFitActivity activity, string sourceFileName)
        {
            ReceivedActivity = activity;
            ReceivedSourceFileName = sourceFileName;
            return Result!;
        }
    }

    private sealed class FakeTrainingActivityRepository : ITrainingActivityRepository
    {
        public Guid SavedUploadId { get; private set; }
        public CleanedActivity? SavedActivity { get; private set; }

        public Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken)
        {
            SavedUploadId = uploadId;
            SavedActivity = activity;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeJobQueue : IJobQueue
    {
        public List<(JobType Type, Guid SubjectId)> EnqueuedIfNotPending { get; } = [];

        public Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid> EnqueueIfNotPendingAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
        {
            EnqueuedIfNotPending.Add((type, subjectId));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CompleteAsync(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static AnalysisJob RunningJob(Guid uploadId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AnalysisJob(
            Guid.NewGuid(),
            JobType.ParseTraining,
            uploadId,
            JobState.Running,
            0,
            "running",
            1,
            now,
            now,
            now,
            null,
            "worker-1",
            now.AddMinutes(5));
    }
}
