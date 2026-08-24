using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Api.Workers;
using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Validation;

namespace RouteTimer.Api.Tests.Workers;

public sealed class AnalysisWorkerTests
{
    [Fact]
    public async Task Claims_a_job_and_dispatches_it_to_the_matching_handler()
    {
        var job = MakeJob(JobType.ParseTraining);
        var jobQueue = new FakeJobQueue();
        jobQueue.EnqueueClaim(job);
        var handler = new FakeJobHandler(JobType.ParseTraining);
        var worker = CreateWorker(jobQueue, handler);

        await worker.ProcessIterationAsync(CancellationToken.None);

        Assert.Single(handler.ReceivedJobs, received => received.Id == job.Id);
    }

    [Fact]
    public async Task Completes_the_job_when_the_handler_succeeds()
    {
        var job = MakeJob(JobType.ParseTraining);
        var jobQueue = new FakeJobQueue();
        jobQueue.EnqueueClaim(job);
        var handler = new FakeJobHandler(JobType.ParseTraining);
        var worker = CreateWorker(jobQueue, handler);

        await worker.ProcessIterationAsync(CancellationToken.None);

        Assert.Contains(job.Id, jobQueue.Completed);
        Assert.Empty(jobQueue.Failed);
    }

    [Fact]
    public async Task Fails_permanently_with_the_exceptions_code_and_message_on_activity_input_exception()
    {
        var job = MakeJob(JobType.ParseTraining);
        var jobQueue = new FakeJobQueue();
        jobQueue.EnqueueClaim(job);
        var handler = new FakeJobHandler(JobType.ParseTraining, new ActivityInputException("corrupt-fit", "The FIT file is corrupt."));
        var worker = CreateWorker(jobQueue, handler);

        await worker.ProcessIterationAsync(CancellationToken.None);

        var failure = Assert.Single(jobQueue.Failed);
        Assert.Equal(job.Id, failure.JobId);
        Assert.True(failure.Permanent);
        Assert.Equal("corrupt-fit", failure.Code);
        Assert.Equal("The FIT file is corrupt.", failure.Message);
        Assert.Empty(jobQueue.Completed);
    }

    [Fact]
    public async Task Fails_transiently_on_an_unexpected_exception_and_keeps_processing_afterward()
    {
        var jobOne = MakeJob(JobType.ParseTraining);
        var jobTwo = MakeJob(JobType.ParseTraining);
        var jobQueue = new FakeJobQueue();
        jobQueue.EnqueueClaim(jobOne);
        jobQueue.EnqueueClaim(jobTwo);
        // First call blows up with an unrelated bug; second call succeeds.
        var handler = new FakeJobHandler(JobType.ParseTraining, new InvalidOperationException("db exploded"), null);
        var worker = CreateWorker(jobQueue, handler);

        await worker.ProcessIterationAsync(CancellationToken.None);
        await worker.ProcessIterationAsync(CancellationToken.None);

        var failure = Assert.Single(jobQueue.Failed);
        Assert.Equal(jobOne.Id, failure.JobId);
        Assert.False(failure.Permanent);
        Assert.Equal("processing-error", failure.Code);
        Assert.Equal("An unexpected error occurred while processing this job.", failure.Message);
        Assert.Contains(jobTwo.Id, jobQueue.Completed);
    }

    [Fact]
    public async Task Fails_permanently_when_no_handler_is_registered_for_the_claimed_job_type()
    {
        var job = MakeJob(JobType.BuildModel);
        var jobQueue = new FakeJobQueue();
        jobQueue.EnqueueClaim(job);
        var worker = CreateWorker(jobQueue);

        await worker.ProcessIterationAsync(CancellationToken.None);

        var failure = Assert.Single(jobQueue.Failed);
        Assert.Equal(job.Id, failure.JobId);
        Assert.True(failure.Permanent);
        Assert.Equal("no-handler", failure.Code);
        Assert.Empty(jobQueue.Completed);
    }

    [Fact]
    public async Task Renews_the_lease_periodically_while_the_handler_runs_and_stops_once_it_finishes()
    {
        var job = MakeJob(JobType.ParseTraining);
        var jobQueue = new FakeJobQueue();
        jobQueue.EnqueueClaim(job);
        var renewCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        jobQueue.OnRenew = () => renewCalled.TrySetResult();
        var handlerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedJobHandler(JobType.ParseTraining, handlerGate.Task);
        var timeProvider = new FakeTimeProvider();
        var worker = CreateWorker(jobQueue, timeProvider, handler);

        // Start the iteration: it claims the job, dispatches to the (still-gated) handler, and the renewal
        // loop suspends on its first Task.Delay - all of this happens synchronously up to that suspension
        // point, so the FakeTimeProvider timer is guaranteed to be registered before we advance below.
        var iterationTask = worker.ProcessIterationAsync(CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromMinutes(3));
        await renewCalled.Task;

        Assert.Contains(jobQueue.Renewed, renewal => renewal.JobId == job.Id && renewal.WorkerId == jobQueue.LastClaimWorkerId);
        var renewalCountWhileRunning = jobQueue.Renewed.Count;

        handlerGate.SetResult();
        await iterationTask;

        Assert.Contains(job.Id, jobQueue.Completed);

        // No further renewals happen once the handler (and thus the renewal loop) has stopped, even if
        // time keeps moving.
        timeProvider.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(renewalCountWhileRunning, jobQueue.Renewed.Count);
    }

    [Fact]
    public async Task Survives_an_exception_thrown_while_claiming_a_job_and_keeps_processing_afterward()
    {
        var jobTwo = MakeJob(JobType.ParseTraining);
        var jobQueue = new FakeJobQueue();
        jobQueue.EnqueueClaimFailure(new InvalidOperationException("claim exploded"));
        jobQueue.EnqueueClaim(jobTwo);
        var handler = new FakeJobHandler(JobType.ParseTraining);
        var worker = CreateWorker(jobQueue, handler);

        // The first call's ClaimAsync throws before any job is dispatched - this must not propagate out of
        // ProcessIterationAsync, and the worker must still be able to process the next claim afterward.
        await worker.ProcessIterationAsync(CancellationToken.None);
        await worker.ProcessIterationAsync(CancellationToken.None);

        Assert.Contains(jobTwo.Id, jobQueue.Completed);
    }

    private static AnalysisJob MakeJob(JobType type) =>
        new(Guid.NewGuid(), type, Guid.NewGuid(), JobState.Running, 1, "worker-1", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

    private static AnalysisWorker CreateWorker(IJobQueue jobQueue, params IJobHandler[] handlers) =>
        CreateWorker(jobQueue, new FakeTimeProvider(), handlers);

    private static AnalysisWorker CreateWorker(IJobQueue jobQueue, TimeProvider timeProvider, params IJobHandler[] handlers)
    {
        var services = new ServiceCollection();
        services.AddSingleton(jobQueue);
        foreach (var handler in handlers)
        {
            services.AddSingleton<IJobHandler>(handler);
        }

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new AnalysisWorker(scopeFactory, timeProvider, NullLogger<AnalysisWorker>.Instance);
    }

    private sealed class FakeJobQueue : IJobQueue
    {
        private readonly Queue<Func<AnalysisJob?>> claims = new();

        public List<Guid> Completed { get; } = [];
        public List<(Guid JobId, bool Permanent, string? Code, string? Message)> Failed { get; } = [];
        public List<(Guid JobId, string WorkerId)> Renewed { get; } = [];
        public string? LastClaimWorkerId { get; private set; }
        public Action? OnRenew { get; set; }

        public void EnqueueClaim(AnalysisJob? job) => claims.Enqueue(() => job);

        public void EnqueueClaimFailure(Exception exception) => claims.Enqueue(() => throw exception);

        public Task<Guid> EnqueueAsync(JobType type, Guid subjectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AnalysisJob?> ClaimAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            LastClaimWorkerId = workerId;
            return Task.FromResult(claims.Count > 0 ? claims.Dequeue()() : null);
        }

        public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Renewed.Add((jobId, workerId));
            OnRenew?.Invoke();
            return Task.FromResult(true);
        }

        public Task<bool> CompleteAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
        {
            Completed.Add(jobId);
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(Guid jobId, string workerId, bool permanent, string? diagnosticCode, string? diagnosticMessage, CancellationToken cancellationToken)
        {
            Failed.Add((jobId, permanent, diagnosticCode, diagnosticMessage));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeJobHandler : IJobHandler
    {
        private readonly Queue<Exception?> behaviors;

        public FakeJobHandler(JobType handles, params Exception?[] behaviors)
        {
            Handles = handles;
            this.behaviors = new Queue<Exception?>(behaviors);
        }

        public JobType Handles { get; }

        public List<AnalysisJob> ReceivedJobs { get; } = [];

        public Task HandleAsync(AnalysisJob job, CancellationToken cancellationToken)
        {
            ReceivedJobs.Add(job);
            var exception = behaviors.Count > 0 ? behaviors.Dequeue() : null;
            if (exception is not null)
            {
                throw exception;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A handler whose completion is controlled by the test via <paramref name="gate"/>.</summary>
    private sealed class GatedJobHandler(JobType handles, Task gate) : IJobHandler
    {
        public JobType Handles { get; } = handles;

        public Task HandleAsync(AnalysisJob job, CancellationToken cancellationToken) => gate;
    }
}
