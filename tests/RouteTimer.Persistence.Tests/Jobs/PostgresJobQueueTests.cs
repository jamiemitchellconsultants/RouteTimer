using RouteTimer.Domain.Jobs;
using RouteTimer.Persistence.Jobs;
using RouteTimer.Services.Jobs;

namespace RouteTimer.Persistence.Tests.Jobs;

public sealed class PostgresJobQueueTests
{
    [Fact]
    public async Task Expired_running_job_can_be_claimed_again()
    {
        var queue = new PostgresJobQueue();
        var now = DateTimeOffset.UtcNow;
        var id = await queue.EnqueueAsync(JobType.ParseTraining, Guid.NewGuid(), CancellationToken.None);
        var first = await queue.ClaimAsync("worker-a", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        var second = await queue.ClaimAsync("worker-b", now.AddMinutes(3), TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(id, first!.Id);
        Assert.Equal(id, second!.Id);
        Assert.Equal(2, second.AttemptCount);
    }
}
