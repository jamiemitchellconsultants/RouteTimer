using System;
using System.Collections.Generic;

namespace RouteTimer.Domain.Adjustments.TimeTarget;

public enum TimeTargetDistribution { Proportional, ClimbFocused }
public enum TimeTargetFeasibilityVerdict { Achievable, Challenging, Extreme, Impossible }

public sealed record TimeTargetDefinition : PacingStrategyDefinition
{
    public const double MinimumTargetSeconds = 1;
    public const double MaximumTargetSeconds = 172800;

    public TimeTargetDefinition(
        double targetMovingSeconds,
        TimeTargetDistribution distribution,
        double? climbBias,
        bool includeFeasibilityReport)
        : base(PacingStrategyType.TimeTarget)
    {
        if (double.IsNaN(targetMovingSeconds) || double.IsInfinity(targetMovingSeconds) || targetMovingSeconds < MinimumTargetSeconds || targetMovingSeconds > MaximumTargetSeconds)
        {
            throw new ArgumentException($"Target moving seconds must be in range [{MinimumTargetSeconds}, {MaximumTargetSeconds}].", nameof(targetMovingSeconds));
        }

        if (!Enum.IsDefined(distribution))
        {
            throw new ArgumentException("Invalid distribution value.", nameof(distribution));
        }

        if (distribution == TimeTargetDistribution.Proportional)
        {
            if (climbBias is not null)
            {
                throw new ArgumentException("Climb bias must be null when distribution is Proportional.", nameof(climbBias));
            }
        }
        else if (distribution == TimeTargetDistribution.ClimbFocused)
        {
            if (climbBias is null || double.IsNaN(climbBias.Value) || double.IsInfinity(climbBias.Value) || climbBias.Value < 1.0 || climbBias.Value > 2.0)
            {
                throw new ArgumentException("Climb bias must be between 1.0 and 2.0 when distribution is ClimbFocused.", nameof(climbBias));
            }
        }

        TargetMovingSeconds = targetMovingSeconds;
        Distribution = distribution;
        ClimbBias = climbBias;
        IncludeFeasibilityReport = includeFeasibilityReport;
    }

    public double TargetMovingSeconds { get; }
    public TimeTargetDistribution Distribution { get; }
    public double? ClimbBias { get; }
    public bool IncludeFeasibilityReport { get; }
}

public sealed record TimeTargetGradientBandReport(
    string GradientBand,
    double MovingSeconds,
    double BaselineEstimateWattSeconds,
    double RequiredWattSeconds,
    double DemandRatio);

public sealed record TimeTargetReport(
    double TargetMovingSeconds,
    double AchievedMovingSeconds,
    double AbsoluteMissSeconds,
    double PercentageMiss,
    TimeTargetDistribution Distribution,
    double SelectedOuterScale,
    double SelectedClimbScale,
    double SelectedOtherScale,
    bool Converged,
    bool Bracketed,
    int EvaluationCount,
    double? FastestBoundSeconds,
    double? SlowestBoundSeconds,
    IReadOnlyList<TimeTargetGradientBandReport> GradientBands,
    TimeTargetFeasibilityVerdict Verdict,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.TimeTarget);
