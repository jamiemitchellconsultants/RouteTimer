using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Validation;

namespace RouteTimer.Api.Workers;

/// <summary>
/// Hosted background worker that continuously claims queued <see cref="AnalysisJob"/>s and dispatches
/// them to the matching <see cref="IJobHandler"/>. Keeps the claimed job's lease alive for the duration
/// of handling, classifies failures into permanent (bad input, no handler) versus transient (unexpected
/// errors, retried by the queue's own bounded-retry policy), and never lets a single job's failure - or
/// a bug in its own dispatch code - take the host down.
/// </summary>
public sealed class AnalysisWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<AnalysisWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromMinutes(2.5);
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(5);

    private readonly string workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected during host shutdown; the loop condition above will exit next check.
            }
            catch (Exception exception)
            {
                // A bug in dispatch/completion code (not the handler itself) must not crash the host.
                logger.LogError(exception, "Unexpected error in the analysis worker loop.");
            }
        }
    }

    internal async Task ProcessIterationAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>();

        var job = await jobs.ClaimAsync(workerId, timeProvider.GetUtcNow(), LeaseDuration, stoppingToken);
        if (job is null)
        {
            await Task.Delay(IdlePollDelay, timeProvider, stoppingToken);
            return;
        }

        var handler = handlers.FirstOrDefault(candidate => candidate.Handles == job.Type);
        if (handler is null)
        {
            await jobs.FailAsync(job.Id, workerId, permanent: true, "no-handler", $"No handler registered for job type {job.Type}.", stoppingToken);
            return;
        }

        using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewalTask = RenewLeaseWhileHandlingAsync(jobs, job.Id, renewalCts.Token);
        try
        {
            await handler.HandleAsync(job, stoppingToken);
            await jobs.CompleteAsync(job.Id, workerId, stoppingToken);
        }
        catch (ActivityInputException exception)
        {
            await jobs.FailAsync(job.Id, workerId, permanent: true, exception.Code, exception.Message, stoppingToken);
        }
        catch (Exception exception)
        {
            // Full detail (including stack trace) goes to the log only - the stored diagnostic must stay
            // safe, generic text; the queue's own bounded-retry logic decides when this becomes terminal.
            logger.LogError(exception, "Unexpected error while processing job {JobId} of type {JobType}.", job.Id, job.Type);
            await jobs.FailAsync(job.Id, workerId, permanent: false, "processing-error", "An unexpected error occurred while processing this job.", stoppingToken);
        }
        finally
        {
            renewalCts.Cancel();
            await renewalTask;
        }
    }

    private async Task RenewLeaseWhileHandlingAsync(IJobQueue jobs, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RenewalInterval, timeProvider, cancellationToken);
                await jobs.RenewLeaseAsync(jobId, workerId, timeProvider.GetUtcNow(), LeaseDuration, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the handler finishes and the iteration cancels this loop.
        }
    }
}
