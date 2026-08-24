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
/// retrying without new data would fail again. Validation is a placeholder only - this handler never
/// performs real leave-one-out validation, it only distinguishes "too little evidence to validate at
/// all" from "evidence exists but hasn't been validated yet"; real validation is separate future work.
/// </summary>
public sealed class BuildModelJobHandler(
    IProfileRepository profiles,
    ITrainingActivityRepository activities,
    IPowerModelBuilder builder,
    IRiderModelRepository models) : IJobHandler
{
    /// <summary>Bump whenever the model-building algorithm or its configuration changes materially.</summary>
    public const string AlgorithmVersion = "power-model-v1";

    private const int MinimumEligibleActivitiesForValidation = 3;

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

        var powerModel = builder.Build(profile, allActivities);
        var model = new RiderModel(powerModel, PhysicalCoefficients.Default, AlgorithmVersion);

        var status = eligibleCount < MinimumEligibleActivitiesForValidation
            ? ModelValidationStatus.InsufficientData
            : ModelValidationStatus.NotValidated;
        var validation = new ModelValidationSummary(status, null, null);

        await models.SaveAsync(model, profile, wasCalibrated: false, validation, cancellationToken);
    }
}
