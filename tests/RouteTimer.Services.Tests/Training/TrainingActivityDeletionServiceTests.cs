using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Training;

namespace RouteTimer.Services.Tests.Training;

public sealed class TrainingActivityDeletionServiceTests
{
    // Break caught: deleting an activity removes only the activity row and does not queue the coalesced model rebuild successor.
    [Fact]
    public async Task Delete_removes_activity_samples_and_source_upload_then_queues_rebuild()
    {
        var activityId = Guid.NewGuid();
        var rebuildJobId = Guid.NewGuid();
        var activities = new FakeTrainingActivityRepository { ExistingActivityId = activityId };
        var jobs = new FakeJobQueue { RebuildJobId = rebuildJobId };
        var service = new TrainingActivityDeletionService(activities, jobs);

        var result = await service.DeleteAsync(activityId, CancellationToken.None);

        Assert.True(result.Deleted);
        Assert.Equal(rebuildJobId, result.RebuildJobId);
        Assert.Equal(activityId, activities.DeletedActivityId);
        var enqueued = Assert.Single(jobs.EnqueuedIfNotPending);
        Assert.Equal(JobType.BuildModel, enqueued.Type);
        Assert.Equal(ModelSubject.Id, enqueued.SubjectId);
    }

    private sealed class FakeTrainingActivityRepository : ITrainingActivityRepository
    {
        public Guid ExistingActivityId { get; init; }
        public Guid? DeletedActivityId { get; private set; }

        public Task<Guid> SaveAsync(Guid uploadId, CleanedActivity activity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CleanedActivity?> GetAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CleanedActivity>> GetAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrainingActivitySummary>> GetSummariesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TrainingActivityDetail?> GetDetailAsync(Guid activityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TrainingActivityCounts> GetCountsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid activityId, CancellationToken cancellationToken)
        {
            if (activityId != ExistingActivityId)
            {
                return Task.FromResult(false);
            }

            DeletedActivityId = activityId;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeJobQueue : IJobQueue
    {
        public Guid RebuildJobId { get; init; }
        public List<(JobType Type, Guid SubjectId)> EnqueuedIfNotPending { get; } = [];

        public Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> EnqueueIfNotPendingAsync(JobType type, Guid subjectId, CancellationToken cancellationToken)
        {
            EnqueuedIfNotPending.Add((type, subjectId));
            return Task.FromResult(RebuildJobId);
        }
        public Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReportProgressAsync(Guid jobId, string workerId, int progressPercent, string stage, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(Guid jobId, string workerId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
