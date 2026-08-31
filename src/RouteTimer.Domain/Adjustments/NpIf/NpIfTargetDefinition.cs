using System;

namespace RouteTimer.Domain.Adjustments.NpIf;

public enum NpIfScalingMode { Proportional, Additive }

public sealed record NpIfTargetDefinition : PacingStrategyDefinition
{
    public NpIfTargetDefinition(double targetIntensityFactor, double ftpWatts, NpIfScalingMode mode)
        : base(PacingStrategyType.NpIfTarget)
    {
        if (double.IsNaN(targetIntensityFactor) || double.IsInfinity(targetIntensityFactor) || targetIntensityFactor <= 0 || targetIntensityFactor > 1.5)
        {
            throw new ArgumentException("Target intensity factor must be in range (0, 1.5].", nameof(targetIntensityFactor));
        }

        if (double.IsNaN(ftpWatts) || double.IsInfinity(ftpWatts) || ftpWatts < 1 || ftpWatts > 2000)
        {
            throw new ArgumentException("FTP watts must be in range [1, 2000].", nameof(ftpWatts));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException("Invalid scaling mode value.", nameof(mode));
        }

        TargetIntensityFactor = targetIntensityFactor;
        FtpWatts = ftpWatts;
        Mode = mode;
    }

    public double TargetIntensityFactor { get; }
    public double FtpWatts { get; }
    public NpIfScalingMode Mode { get; }
}

public sealed record NpIfTargetReport(
    double TargetNormalizedPowerWatts,
    double AchievedNormalizedPowerWatts,
    double TargetIntensityFactor,
    double AchievedIntensityFactor,
    double FtpWatts,
    NpIfScalingMode Mode,
    double SelectedParameter,
    bool Converged,
    bool Bracketed,
    int EvaluationCount,
    bool UsedShortRouteApproximation,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.NpIfTarget);
