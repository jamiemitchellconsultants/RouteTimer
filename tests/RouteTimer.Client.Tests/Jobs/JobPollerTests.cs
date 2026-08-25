using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Client.Jobs;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Jobs;

namespace RouteTimer.Client.Tests.Jobs;

public sealed class JobPollerTests
{
    [Fact]
    public async Task Poller_requests_immediately_then_stops_on_success()
    {
        var api = new FakeRouteTimerApiClient();
        api.Jobs.Enqueue(Job("Running", 25, "processing-route"));
        api.Jobs.Enqueue(Job("Succeeded", 100, "completed"));
        var time = new FakeTimeProvider();
        var updates = new List<JobResponse>();

        var task = new JobPoller(api, time).PollAsync(
            Guid.NewGuid(),
            job =>
            {
                updates.Add(job);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await Task.Yield();
        Assert.Single(updates);
        Assert.Equal("processing-route", updates[0].ProgressStage);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(JobPollOutcome.Succeeded, await task);
        Assert.Equal(2, updates.Count);
        Assert.Equal("completed", updates[1].ProgressStage);
        Assert.Equal(2, api.RequestedJobs.Count);
    }

    [Theory]
    [InlineData("failed", JobPollOutcome.Failed)]
    [InlineData("CANCELLED", JobPollOutcome.Cancelled)]
    public async Task Poller_treats_terminal_states_case_insensitively(string state, JobPollOutcome expectedOutcome)
    {
        var api = new FakeRouteTimerApiClient();
        api.Jobs.Enqueue(Job("Running", 45, "simulating-route"));
        api.Jobs.Enqueue(Job(state, 45, state.ToLowerInvariant()));
        var time = new FakeTimeProvider();

        var task = new JobPoller(api, time).PollAsync(Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None);

        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(expectedOutcome, await task);
        Assert.Equal(2, api.RequestedJobs.Count);
    }

    [Fact]
    public async Task Poller_returns_removed_after_two_consecutive_missing_jobs()
    {
        var api = new FakeRouteTimerApiClient();
        api.Jobs.Enqueue(null);
        api.Jobs.Enqueue(null);
        var time = new FakeTimeProvider();

        var task = new JobPoller(api, time).PollAsync(Guid.NewGuid(), _ => Task.CompletedTask, CancellationToken.None);

        await Task.Yield();
        Assert.False(task.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(JobPollOutcome.Removed, await task);
        Assert.Equal(2, api.RequestedJobs.Count);
    }

    [Fact]
    public async Task Poller_resets_missing_counter_after_a_non_null_update()
    {
        var api = new FakeRouteTimerApiClient();
        api.Jobs.Enqueue(null);
        api.Jobs.Enqueue(Job("Running", 65, "saving-result"));
        api.Jobs.Enqueue(null);
        api.Jobs.Enqueue(null);
        var time = new FakeTimeProvider();
        var updates = new List<JobResponse>();

        var task = new JobPoller(api, time).PollAsync(
            Guid.NewGuid(),
            job =>
            {
                updates.Add(job);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(JobPollOutcome.Removed, await task);
        Assert.Single(updates);
        Assert.Equal("saving-result", updates[0].ProgressStage);
        Assert.Equal(4, api.RequestedJobs.Count);
    }

    [Fact]
    public async Task Poller_propagates_caller_cancellation_while_waiting_for_the_next_interval()
    {
        var api = new FakeRouteTimerApiClient();
        api.Jobs.Enqueue(Job("Running", 25, "processing-route"));
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var task = new JobPoller(api, time).PollAsync(Guid.NewGuid(), _ => Task.CompletedTask, cts.Token);

        await Task.Yield();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Single(api.RequestedJobs);
        Assert.Equal(cts.Token, api.RequestedJobs[0].CancellationToken);
    }

    private static JobResponse Job(string state, int progressPercent, string stage) => new(
        Guid.NewGuid(),
        "PredictRoute",
        Guid.NewGuid(),
        state,
        progressPercent,
        stage,
        1,
        DateTimeOffset.Parse("2026-08-25T10:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T10:01:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T10:02:00Z", CultureInfo.InvariantCulture),
        state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.Parse("2026-08-25T10:03:00Z", CultureInfo.InvariantCulture)
            : null,
        null,
        state.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "prediction-failed" : null,
        state.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "Route processing failed safely." : null);
}
