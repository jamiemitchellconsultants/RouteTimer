using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Models;

/// <summary>
/// Processes a <see cref="JobType.BuildModel"/> job: loads the rider profile and every persisted
/// training activity, builds a fresh <see cref="PowerModel"/> from the eligible evidence, and persists
/// the resulting <see cref="RiderModel"/> version. Missing prerequisites (no profile, no eligible
/// evidence) are reported as a permanent <see cref="ModelBuildException"/> rather than retried, since
/// retrying without new data would fail again. Real leave-one-out validation
/// (see <see cref="IModelValidator"/>) runs as part of every model build, comparing the model's
/// predictions against each eligible activity's own recorded moving time.
/// </summary>
public sealed class BuildModelJobHandler(
    IProfileRepository profiles,
    ITrainingActivityRepository activities,
    IPowerModelBuilder builder,
    IModelValidator validator,
    IRiderModelRepository models) : IJobHandler
{
    /// <summary>Bump whenever the model-building algorithm or its configuration changes materially.</summary>
    public const string AlgorithmVersion = "power-model-v1";

    public JobType Handles => JobType.BuildModel;

    public async Task HandleAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var profile = await profiles.GetAsync(cancellationToken);
        if (profile is null)
        {
            throw new ModelBuildException("profile-missing", "A rider profile is required before a model can be built.");
        }

        var allActivities = await activities.GetAllAsync(cancellationToken);
        var eligibleCount = allActivities.Count(activity => activity.Quality.Eligibility == ActivityEligibility.Eligible);
        if (eligibleCount == 0)
        {
            throw new ModelBuildException("no-eligible-activities", "No eligible training activities are available to build a model.");
        }

        // eligibleCount > 0 does not, by itself, guarantee the builder finds power evidence: today
        // TrainingCleaner's eligibility thresholds happen to make the two equivalent, but that's an
        // implicit cross-module invariant nothing enforces here. Translate the builder's generic
        // InvalidOperationException into a permanent ModelBuildException rather than letting it fall
        // through to AnalysisWorker's transient-failure path, where it would be retried three times
        // (and logged as an error each time) before failing with an unhelpful generic diagnostic.
        PowerModel powerModel;
        try
        {
            powerModel = builder.Build(profile, allActivities);
        }
        catch (InvalidOperationException)
        {
            throw new ModelBuildException("no-power-evidence", "No eligible training activities have power data available.");
        }

        var model = new RiderModel(powerModel, PhysicalCoefficients.Default, AlgorithmVersion);

        var validation = validator.Validate(profile, allActivities);

        // Calibration (fitting the physical coefficients to observed data) isn't implemented yet, so
        // this handler always saves the default coefficients uncalibrated - that's expected to change
        // once calibration is built.
        await models.SaveAsync(model, profile, wasCalibrated: false, validation, cancellationToken);
    }
}
