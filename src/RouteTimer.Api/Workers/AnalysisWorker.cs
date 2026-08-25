using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Validation;

namespace RouteTimer.Api.Workers;

/// <summary>
/// Hosted background worker that continuously claims queued <see cref="AnalysisJob"/>s and dispatches
/// them to the matching <see cref="IJobHandler"/>. Keeps the claimed job's lease alive for the duration
/// of handling, classifies failures into permanent (a <see cref="RouteTimerJobException"/> - bad input,
/// no handler) versus transient (unexpected errors, retried by the queue's own bounded-retry policy), and
/// never lets a single job's failure - or a bug in its own dispatch code - take the host down.
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
            await ProcessIterationAsync(stoppingToken);
        }
    }

    /// <summary>
    /// One full unit of work - claim, dispatch, complete/fail - wrapped in its own top-level safety net so
    /// that a bug anywhere in this method's own code (not just a handler failure, which is handled by the
    /// inner try/catch below) can never take the host down or stop subsequent iterations from running.
    /// </summary>
    internal async Task ProcessIterationAsync(CancellationToken stoppingToken)
    {
        try
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
                LogIfNoLongerOwned(await jobs.FailAsync(job.Id, workerId, permanent: true, "no-handler", $"No handler registered for job type {job.Type}.", timeProvider.GetUtcNow(), stoppingToken), job.Id);
                return;
            }

            // The lease-renewal loop resolves its own IJobQueue from a scope separate from the one used to
            // claim/dispatch/complete this job. IJobQueue implementations are typically backed by a
            // per-scope EF Core DbContext, which is not safe for overlapping operations - sharing one scope
            // between the renewal loop and the handler's own DB work (e.g. reading the upload, saving the
            // parsed activity) would let the two race on the same DbContext for any job that outlives the
            // renewal interval.
            using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await using var renewalScope = scopeFactory.CreateAsyncScope();
            var renewalJobs = renewalScope.ServiceProvider.GetRequiredService<IJobQueue>();
            var renewalTask = RenewLeaseWhileHandlingAsync(renewalJobs, job.Id, renewalCts.Token);
            try
            {
                await handler.HandleAsync(job, stoppingToken);
                LogIfNoLongerOwned(await jobs.CompleteAsync(job.Id, workerId, timeProvider.GetUtcNow(), stoppingToken), job.Id);
            }
            catch (RouteTimerJobException exception)
            {
                LogIfNoLongerOwned(await jobs.FailAsync(job.Id, workerId, permanent: true, exception.Code, exception.Message, timeProvider.GetUtcNow(), stoppingToken), job.Id);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Job {JobId} stopped because this worker no longer owned it.", job.Id);
            }
            catch (Exception exception)
            {
                // Full detail (including stack trace) goes to the log only - the stored diagnostic must stay
                // safe, generic text; the queue's own bounded-retry logic decides when this becomes terminal.
                logger.LogError(exception, "Unexpected error while processing job {JobId} of type {JobType}.", job.Id, job.Type);
                LogIfNoLongerOwned(await jobs.FailAsync(job.Id, workerId, permanent: false, "processing-error", "An unexpected error occurred while processing this job.", timeProvider.GetUtcNow(), stoppingToken), job.Id);
            }
            finally
            {
                renewalCts.Cancel();
                await renewalTask;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown.
        }
        catch (Exception exception)
        {
            // A bug in this method's own claim/dispatch/completion code (not the handler itself, which is
            // caught above) must not crash the host or stop the next iteration from running.
            logger.LogError(exception, "Unexpected error in the analysis worker loop.");
        }
    }

    private async Task RenewLeaseWhileHandlingAsync(IJobQueue jobs, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RenewalInterval, timeProvider, cancellationToken);
                try
                {
                    await jobs.RenewLeaseAsync(jobId, workerId, timeProvider.GetUtcNow(), LeaseDuration, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A single failed renewal attempt (e.g. a transient DB blip) must not kill the loop -
                    // letting the lease expire while the handler is still running would let another worker
                    // reclaim and duplicate-process this still-in-progress job. Try again next interval.
                    logger.LogWarning(exception, "Failed to renew the lease for job {JobId}; will retry on the next interval.", jobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the handler finishes and the iteration cancels this loop.
        }
    }

    private void LogIfNoLongerOwned(bool ownedByThisWorker, Guid jobId)
    {
        if (!ownedByThisWorker)
        {
            logger.LogWarning("Job {JobId} was not updated: this worker no longer owned it (its lease likely expired and was reclaimed by another worker).", jobId);
        }
    }
}
