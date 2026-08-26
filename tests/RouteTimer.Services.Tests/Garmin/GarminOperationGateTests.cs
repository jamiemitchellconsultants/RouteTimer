using RouteTimer.Services.Garmin;

namespace RouteTimer.Services.Tests.Garmin;

public sealed class GarminOperationGateTests
{
    [Fact]
    public async Task RunAsync_serializes_token_operations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gate = new GarminOperationGate();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var first = gate.RunAsync(async cancellationToken =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return 1;
        }, timeout.Token);

        await firstEntered.Task.WaitAsync(timeout.Token);
        var second = gate.RunAsync(_ =>
        {
            secondEntered = true;
            return Task.FromResult(2);
        }, timeout.Token);

        await Task.Yield();
        Assert.False(secondEntered);

        releaseFirst.SetResult();

        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task RunAsync_releases_the_gate_when_an_operation_throws()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gate = new GarminOperationGate();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync<int>(
            _ => throw new InvalidOperationException("failure"),
            timeout.Token));

        Assert.Equal(42, await gate.RunAsync(_ => Task.FromResult(42), timeout.Token));
    }

    [Fact]
    public async Task RunAsync_passes_the_callers_cancellation_token_to_the_operation()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new GarminOperationGate();

        var receivedToken = await gate.RunAsync(Task.FromResult, cancellation.Token);

        Assert.Equal(cancellation.Token, receivedToken);
    }

    [Fact]
    public async Task RunAsync_does_not_release_a_slot_when_waiting_is_cancelled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gate = new GarminOperationGate();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.RunAsync(async cancellationToken =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            return 1;
        }, timeout.Token);
        await firstEntered.Task.WaitAsync(timeout.Token);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunAsync(_ => Task.FromResult(2), cancellation.Token));

        var thirdEntered = false;
        var third = gate.RunAsync(_ =>
        {
            thirdEntered = true;
            return Task.FromResult(3);
        }, timeout.Token);
        await Task.Yield();
        Assert.False(thirdEntered);

        releaseFirst.SetResult();

        Assert.Equal(1, await first);
        Assert.Equal(3, await third);
        Assert.True(thirdEntered);
    }
}
