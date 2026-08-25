using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Models;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Tests.Models;

public sealed class ModelRebuildServiceTests
{
    [Fact]
    public async Task RequestAsync_rejects_rebuild_when_profile_is_missing()
    {
        var jobs = new FakeJobQueue();
        var service = CreateService(hasProfile: false, counts: new TrainingActivityCounts(0, 0), jobs: jobs);

        var exception = await Assert.ThrowsAsync<ModelRebuildRequestException>(() => service.RequestAsync(CancellationToken.None));

        Assert.Equal("profile-required", exception.Code);
        Assert.Empty(jobs.EnqueueIfNotPendingCalls);
    }

    [Fact]
    public async Task RequestAsync_rejects_rebuild_when_no_training_activity_is_eligible()
    {
        var jobs = new FakeJobQueue();
        var service = CreateService(counts: new TrainingActivityCounts(4, 0), jobs: jobs);

        var exception = await Assert.ThrowsAsync<ModelRebuildRequestException>(() => service.RequestAsync(CancellationToken.None));

        Assert.Equal("no-eligible-activities", exception.Code);
        Assert.Empty(jobs.EnqueueIfNotPendingCalls);
    }

    [Fact]
    public async Task RequestAsync_coalesces_build_model_job_and_returns_the_job_id()
    {
        var existingJobId = Guid.NewGuid();
        var jobs = new FakeJobQueue { EnqueueIfNotPendingResult = existingJobId };
        var service = CreateService(jobs: jobs);

        var jobId = await service.RequestAsync(CancellationToken.None);

        Assert.Equal(existingJobId, jobId);
        Assert.Equal([(JobType.BuildModel, ModelSubject.Id)], jobs.EnqueueIfNotPendingCalls);
    }

    private static ModelRebuildService CreateService(
        bool hasProfile = true,
        TrainingActivityCounts? counts = null,
        FakeJobQueue? jobs = null) =>
        new(
            new FakeProfileRepository { Profile = hasProfile ? new RiderProfile(75, 10) : null },
            new FakeTrainingActivityRepository { Counts = counts ?? new TrainingActivityCounts(3, 2) },
            jobs ?? new FakeJobQueue());

    private sealed class FakeProfileRepository : IProfileRepository
    {
        public RiderProfile? Profile { get; init; }

        public Task<RiderProfile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTrainingActivityRepository : ITrainingActivityRepository
    {
        public TrainingActivityCounts Counts { get; init; } = new(0, 0);

        public Task<Guid> SaveAsync(Guid uploadId, RouteTimer.Domain.Activities.CleanedActivity activity, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RouteTimer.Domain.Activities.CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RouteTimer.Domain.Activities.CleanedActivity>> GetAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TrainingActivityDetail?> GetDetailAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TrainingActivityCounts> GetCountsAsync(CancellationToken cancellationToken) => Task.FromResult(Counts);

        public Task<bool> DeleteAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeJobQueue : IJobQueue
    {
        public Guid EnqueueIfNotPendingResult { get; init; } = Guid.NewGuid();
        public List<(JobType Type, Guid SubjectId)> EnqueueIfNotPendingCalls { get; } = [];

        public Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid> EnqueueIfNotPendingAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
        {
            EnqueueIfNotPendingCalls.Add((type, subjectId));
            return Task.FromResult(EnqueueIfNotPendingResult);
        }

        public Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReportProgressAsync(Guid jobId, string workerId, int progressPercent, string stage, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CancelAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CompleteAsync(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
