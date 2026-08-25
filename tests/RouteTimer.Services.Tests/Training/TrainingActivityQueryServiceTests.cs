using RouteTimer.Domain.Activities;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Training;

namespace RouteTimer.Services.Tests.Training;

public sealed class TrainingActivityQueryServiceTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Earlier.AddHours(1);

    // Break caught: the summary list loads activity samples and sorts by insertion order instead of newest persisted activity first.
    [Fact]
    public async Task List_is_newest_first_and_does_not_load_sample_payloads()
    {
        var repository = new FakeTrainingActivityRepository
        {
            Summaries =
            [
                Summary("older.fit", Earlier),
                Summary("newer.fit", Later)
            ]
        };
        var service = new TrainingActivityQueryService(repository);

        var summaries = await service.GetSummariesAsync(CancellationToken.None);

        Assert.Collection(summaries,
            summary => Assert.Equal("newer.fit", summary.Metadata.SourceFileName),
            summary => Assert.Equal("older.fit", summary.Metadata.SourceFileName));
        Assert.False(repository.LoadedSamplePayloads);
    }

    // Break caught: detail projection strips metadata, quality reason codes, or exclusion counts needed by the API detail endpoint.
    [Fact]
    public async Task Detail_exposes_quality_metadata_exclusions_and_reasons()
    {
        var id = Guid.NewGuid();
        var detail = new TrainingActivityDetail(
            Summary("ride.fit", Later, id, ActivityEligibility.Ineligible, ["low-power-coverage", "position-gap"]),
            new Dictionary<string, int> { ["pause"] = 2, ["gap"] = 1 });
        var repository = new FakeTrainingActivityRepository { Detail = detail };
        var service = new TrainingActivityQueryService(repository);

        var loaded = await service.GetAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Summary.Id);
        Assert.Equal("ride.fit", loaded.Summary.Metadata.SourceFileName);
        Assert.Equal(Later.AddMinutes(-30), loaded.Summary.Metadata.StartedAt);
        Assert.Equal(Later, loaded.Summary.Metadata.EndedAt);
        Assert.Equal("Garmin", loaded.Summary.Metadata.DeviceManufacturer);
        Assert.Equal("Edge", loaded.Summary.Metadata.DeviceProduct);
        Assert.Equal(10_000, loaded.Summary.Metadata.DistanceMetres);
        Assert.Equal(250, loaded.Summary.Metadata.AscentMetres);
        Assert.Equal(ActivityEligibility.Ineligible, loaded.Summary.Eligibility);
        Assert.Equal(new[] { "low-power-coverage", "position-gap" }, loaded.Summary.ReasonCodes);
        Assert.Equal(new Dictionary<string, int> { ["pause"] = 2, ["gap"] = 1 }, loaded.ExclusionCounts);
    }

    private static TrainingActivitySummary Summary(
        string fileName,
        DateTimeOffset createdAt,
        Guid? id = null,
        ActivityEligibility eligibility = ActivityEligibility.Eligible,
        IReadOnlyList<string>? reasonCodes = null) =>
        new(
            id ?? Guid.NewGuid(),
            Guid.NewGuid(),
            new TrainingActivityMetadata(fileName, createdAt.AddMinutes(-30), createdAt, "Garmin", "Edge", 10_000, 250),
            TimeSpan.FromMinutes(30),
            eligibility,
            0.9,
            0.8,
            0.7,
            0.6,
            reasonCodes ?? [],
            createdAt);

    private sealed class FakeTrainingActivityRepository : ITrainingActivityRepository
    {
        public IReadOnlyList<TrainingActivitySummary> Summaries { get; init; } = [];
        public TrainingActivityDetail? Detail { get; init; }
        public bool LoadedSamplePayloads { get; private set; }

        public Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken)
        {
            LoadedSamplePayloads = true;
            throw new NotSupportedException();
        }
        public Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken)
        {
            LoadedSamplePayloads = true;
            throw new NotSupportedException();
        }
        public Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TrainingActivitySummary>>(Summaries.OrderByDescending(summary => summary.CreatedAt).ToList());
        public Task<TrainingActivityDetail?> GetDetailAsync(Guid activityId, CancellationToken cancellationToken) => Task.FromResult(Detail);
        public Task<TrainingActivityCounts> GetCountsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
