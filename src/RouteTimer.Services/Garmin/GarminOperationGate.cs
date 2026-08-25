namespace RouteTimer.Services.Garmin;

public sealed class GarminOperationGate
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
