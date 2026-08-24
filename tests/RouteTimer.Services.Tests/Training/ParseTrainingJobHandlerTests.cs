using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Training;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Training;

public sealed class ParseTrainingJobHandlerTests
{
    private static readonly ParsedFitActivity SampleParsed = new(
        "Morning Ride",
        ActivitySport.Cycling,
        DateTimeOffset.UtcNow,
        [],
        TimeSpan.FromMinutes(30),
        10_000);

    private static readonly CleanedActivity SampleCleaned = new(
        "Morning Ride",
        [],
        TimeSpan.FromMinutes(25),
        new ActivityQuality(ActivityEligibility.Eligible, 1, 1, 1, 1, new Dictionary<string, int>(), []));

    [Fact]
    public async Task Handle_parses_cleans_and_saves_the_upload_referenced_by_the_job()
    {
        var uploadId = Guid.NewGuid();
        var uploads = new FakeStoredUploadRepository { Upload = new StoredUpload(uploadId, "ride.fit", "fit", [1, 2, 3], [], DateTimeOffset.UtcNow) };
        var parser = new FakeFitActivityParser { Result = SampleParsed };
        var cleaner = new FakeTrainingCleaner { Result = SampleCleaned };
        var activities = new FakeTrainingActivityRepository();
        var handler = new ParseTrainingJobHandler(uploads, parser, cleaner, activities);
        var job = new AnalysisJob(Guid.NewGuid(), JobType.ParseTraining, uploadId, JobState.Running, 1, "worker-1", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

        await handler.HandleAsync(job, CancellationToken.None);

        Assert.Same(SampleParsed, cleaner.ReceivedActivity);
        Assert.Equal(uploadId, activities.SavedUploadId);
        Assert.Same(SampleCleaned, activities.SavedActivity);
    }

    [Fact]
    public async Task Handle_throws_permanent_activity_input_exception_when_upload_is_missing()
    {
        var uploads = new FakeStoredUploadRepository { Upload = null };
        var handler = new ParseTrainingJobHandler(uploads, new FakeFitActivityParser(), new FakeTrainingCleaner(), new FakeTrainingActivityRepository());
        var job = new AnalysisJob(Guid.NewGuid(), JobType.ParseTraining, Guid.NewGuid(), JobState.Running, 1, "worker-1", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<ActivityInputException>(() => handler.HandleAsync(job, CancellationToken.None));

        Assert.Equal("upload-missing", exception.Code);
    }

    [Fact]
    public async Task Handle_lets_parser_failures_propagate_unchanged()
    {
        var uploadId = Guid.NewGuid();
        var uploads = new FakeStoredUploadRepository { Upload = new StoredUpload(uploadId, "ride.fit", "fit", [1, 2, 3], [], DateTimeOffset.UtcNow) };
        var parser = new FakeFitActivityParser { ThrownException = new ActivityInputException("corrupt-fit", "The FIT file is corrupt.") };
        var handler = new ParseTrainingJobHandler(uploads, parser, new FakeTrainingCleaner(), new FakeTrainingActivityRepository());
        var job = new AnalysisJob(Guid.NewGuid(), JobType.ParseTraining, uploadId, JobState.Running, 1, "worker-1", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

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

        public CleanedActivity Clean(ParsedFitActivity activity)
        {
            ReceivedActivity = activity;
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
    }
}
