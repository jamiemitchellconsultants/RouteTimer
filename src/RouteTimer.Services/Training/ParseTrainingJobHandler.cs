using RouteTimer.Domain.Jobs;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Training;

/// <summary>
/// Processes a <see cref="JobType.ParseTraining"/> job: decodes the retained FIT upload the job
/// references, cleans it into a training-eligible (or ineligible) activity, and persists the result.
/// Any <see cref="ActivityInputException"/> raised here is a permanent failure the caller (the hosted
/// worker) is responsible for reporting; unexpected exceptions are left to propagate unclassified.
/// On a successful save, coalesces a <see cref="JobType.BuildModel"/> rebuild via
/// <see cref="IJobQueue.EnqueueIfNotPendingAsync"/> - at most one queued rebuild is ever pending, so a
/// batch of uploads finishing in quick succession triggers at most one queued successor rather than one
/// per file, even while another rebuild is already running.
/// </summary>
public sealed class ParseTrainingJobHandler(
    IStoredUploadRepository uploads,
    IFitActivityParser parser,
    ITrainingCleaner cleaner,
    ITrainingActivityRepository activities,
    IJobQueue jobs) : IJobHandler
{
    public JobType Handles => JobType.ParseTraining;

    public async Task HandleAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var upload = await uploads.GetAsync(job.SubjectId, cancellationToken);
        if (upload is null)
        {
            throw new ActivityInputException("upload-missing", "The referenced upload no longer exists.");
        }

        using var content = new MemoryStream(upload.Content);
        var parsed = await parser.ParseAsync(content, cancellationToken);
        var cleaned = cleaner.Clean(parsed, upload.FileName);
        await activities.SaveAsync(job.SubjectId, cleaned, cancellationToken);
        await jobs.EnqueueIfNotPendingAsync(JobType.BuildModel, ModelSubject.Id, cancellationToken);
    }
}
