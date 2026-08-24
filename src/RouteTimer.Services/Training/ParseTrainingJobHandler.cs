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
/// </summary>
public sealed class ParseTrainingJobHandler(
    IStoredUploadRepository uploads,
    IFitActivityParser parser,
    ITrainingCleaner cleaner,
    ITrainingActivityRepository activities) : IJobHandler
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
        var cleaned = cleaner.Clean(parsed);
        await activities.SaveAsync(job.SubjectId, cleaned, cancellationToken);
    }
}
