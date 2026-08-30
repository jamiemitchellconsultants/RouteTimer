using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;

namespace RouteTimer.Domain.Adjustments.MatchBurning;

public enum MatchBurnSelector { Gradient, Distance, Sequence }
public enum MatchBurnIntensity { AbsoluteWatts, PercentCp, CpZone }
public enum MatchPhase { Baseline, Conservation, Recovery, Burn }
public enum CapacityProvenance { Supplied, InferredModel, Fallback }
public enum MatchBurningVerdict { Manageable, Aggressive, Risky, Infeasible }

public sealed record MatchBurnWindow
{
    [JsonConstructor]
    public MatchBurnWindow(
        double? minGradient, double? maxGradient,
        double? minDistanceMetres, double? maxDistanceMetres,
        int? minSequence, int? maxSequence,
        double? absoluteWatts, double? percentCp, int? cpZone)
    {
        int selectorCount = 0;
        if (minGradient is not null || maxGradient is not null)
        {
            selectorCount++;
            Selector = MatchBurnSelector.Gradient;
            if (minGradient is not null && (double.IsNaN(minGradient.Value) || double.IsInfinity(minGradient.Value)))
            {
                throw new ArgumentException("Min gradient must be a finite number.");
            }
            if (maxGradient is not null && (double.IsNaN(maxGradient.Value) || double.IsInfinity(maxGradient.Value)))
            {
                throw new ArgumentException("Max gradient must be a finite number.");
            }
            if (minGradient is not null && maxGradient is not null && minGradient.Value > maxGradient.Value)
            {
                throw new ArgumentException("Min gradient cannot be greater than max gradient.");
            }
        }

        if (minDistanceMetres is not null || maxDistanceMetres is not null)
        {
            selectorCount++;
            Selector = MatchBurnSelector.Distance;
            if (minDistanceMetres is not null && (double.IsNaN(minDistanceMetres.Value) || double.IsInfinity(minDistanceMetres.Value) || minDistanceMetres.Value < 0))
            {
                throw new ArgumentException("Min distance metres must be non-negative.");
            }
            if (maxDistanceMetres is not null && (double.IsNaN(maxDistanceMetres.Value) || double.IsInfinity(maxDistanceMetres.Value) || maxDistanceMetres.Value < 0))
            {
                throw new ArgumentException("Max distance metres must be non-negative.");
            }
            if (minDistanceMetres is not null && maxDistanceMetres is not null && minDistanceMetres.Value > maxDistanceMetres.Value)
            {
                throw new ArgumentException("Min distance metres cannot be greater than max distance metres.");
            }
        }

        if (minSequence is not null || maxSequence is not null)
        {
            selectorCount++;
            Selector = MatchBurnSelector.Sequence;
            if (minSequence is not null && minSequence.Value < 1)
            {
                throw new ArgumentException("Min sequence must be at least 1.");
            }
            if (maxSequence is not null && maxSequence.Value < 1)
            {
                throw new ArgumentException("Max sequence must be at least 1.");
            }
            if (minSequence is not null && maxSequence is not null && minSequence.Value > maxSequence.Value)
            {
                throw new ArgumentException("Min sequence cannot be greater than max sequence.");
            }
        }

        if (selectorCount != 1)
        {
            throw new ArgumentException("Exactly one selector family (gradient, distance, or sequence) must be specified.");
        }

        int intensityCount = 0;
        if (absoluteWatts is not null)
        {
            intensityCount++;
            Intensity = MatchBurnIntensity.AbsoluteWatts;
            if (double.IsNaN(absoluteWatts.Value) || double.IsInfinity(absoluteWatts.Value) || absoluteWatts.Value < 10 || absoluteWatts.Value > 2000)
            {
                throw new ArgumentException("Absolute watts must be in range [10, 2000].");
            }
        }
        if (percentCp is not null)
        {
            intensityCount++;
            Intensity = MatchBurnIntensity.PercentCp;
            if (double.IsNaN(percentCp.Value) || double.IsInfinity(percentCp.Value) || percentCp.Value < 0.5 || percentCp.Value > 3.0)
            {
                throw new ArgumentException("Percent CP must be in range [0.5, 3.0].");
            }
        }
        if (cpZone is not null)
        {
            intensityCount++;
            Intensity = MatchBurnIntensity.CpZone;
            if (cpZone.Value < 1 || cpZone.Value > 7)
            {
                throw new ArgumentException("CP zone must be in range [1, 7].");
            }
        }

        if (intensityCount != 1)
        {
            throw new ArgumentException("Exactly one intensity (absoluteWatts, percentCp, or cpZone) must be specified.");
        }

        MinGradient = minGradient;
        MaxGradient = maxGradient;
        MinDistanceMetres = minDistanceMetres;
        MaxDistanceMetres = maxDistanceMetres;
        MinSequence = minSequence;
        MaxSequence = maxSequence;
        AbsoluteWatts = absoluteWatts;
        PercentCp = percentCp;
        CpZone = cpZone;
    }

    public MatchBurnSelector Selector { get; }
    public MatchBurnIntensity Intensity { get; }
    public double? MinGradient { get; }
    public double? MaxGradient { get; }
    public double? MinDistanceMetres { get; }
    public double? MaxDistanceMetres { get; }
    public int? MinSequence { get; }
    public int? MaxSequence { get; }
    public double? AbsoluteWatts { get; }
    public double? PercentCp { get; }
    public int? CpZone { get; }

    public bool Matches(PredictionRouteSegment segment)
    {
        return Selector switch
        {
            MatchBurnSelector.Gradient => (MinGradient is null || segment.Gradient >= MinGradient.Value) &&
                                          (MaxGradient is null || segment.Gradient <= MaxGradient.Value),
            MatchBurnSelector.Distance => (MinDistanceMetres is null || segment.CumulativeDistanceMetres >= MinDistanceMetres.Value) &&
                                          (MaxDistanceMetres is null || segment.CumulativeDistanceMetres <= MaxDistanceMetres.Value),
            MatchBurnSelector.Sequence => (MinSequence is null || segment.Sequence >= MinSequence.Value) &&
                                          (MaxSequence is null || segment.Sequence <= MaxSequence.Value),
            _ => false
        };
    }
}

public sealed record MatchBurningDefinition : PacingStrategyDefinition
{
    public const int MaximumWindows = 10;

    public MatchBurningDefinition(
        double? criticalPowerWatts,
        double? wPrimeJoules,
        IReadOnlyList<MatchBurnWindow> windows,
        double conservationDurationSeconds,
        double conservationTargetCpFraction,
        double recoveryDurationSeconds,
        double recoveryTargetCpFraction,
        bool includeFatigueReport,
        bool enableRefinement)
        : base(PacingStrategyType.VariableMatchBurning)
    {
        if (criticalPowerWatts is not null && (double.IsNaN(criticalPowerWatts.Value) || double.IsInfinity(criticalPowerWatts.Value) || criticalPowerWatts.Value < 1 || criticalPowerWatts.Value > 2000))
        {
            throw new ArgumentException("Critical power watts must be in range [1, 2000].", nameof(criticalPowerWatts));
        }

        if (wPrimeJoules is not null && (double.IsNaN(wPrimeJoules.Value) || double.IsInfinity(wPrimeJoules.Value) || wPrimeJoules.Value < 1000 || wPrimeJoules.Value > 100000))
        {
            throw new ArgumentException("W-prime joules must be in range [1000, 100000].", nameof(wPrimeJoules));
        }

        if (windows is null || windows.Count == 0 || windows.Count > MaximumWindows)
        {
            throw new ArgumentException($"Windows list must contain between 1 and {MaximumWindows} items.", nameof(windows));
        }

        if (windows.Any(w => w is null))
        {
            throw new ArgumentException("Windows list must not contain null items.", nameof(windows));
        }

        if (double.IsNaN(conservationDurationSeconds) || double.IsInfinity(conservationDurationSeconds) || conservationDurationSeconds < 0 || conservationDurationSeconds > 300)
        {
            throw new ArgumentException("Conservation duration seconds must be in range [0, 300].", nameof(conservationDurationSeconds));
        }

        if (double.IsNaN(conservationTargetCpFraction) || double.IsInfinity(conservationTargetCpFraction) || conservationTargetCpFraction < 0.5 || conservationTargetCpFraction > 1.0)
        {
            throw new ArgumentException("Conservation target CP fraction must be in range [0.5, 1.0].", nameof(conservationTargetCpFraction));
        }

        if (double.IsNaN(recoveryDurationSeconds) || double.IsInfinity(recoveryDurationSeconds) || recoveryDurationSeconds < 0 || recoveryDurationSeconds > 600)
        {
            throw new ArgumentException("Recovery duration seconds must be in range [0, 600].", nameof(recoveryDurationSeconds));
        }

        if (double.IsNaN(recoveryTargetCpFraction) || double.IsInfinity(recoveryTargetCpFraction) || recoveryTargetCpFraction < 0.5 || recoveryTargetCpFraction > 0.9)
        {
            throw new ArgumentException("Recovery target CP fraction must be in range [0.5, 0.9].", nameof(recoveryTargetCpFraction));
        }

        CriticalPowerWatts = criticalPowerWatts;
        WPrimeJoules = wPrimeJoules;
        Windows = windows;
        ConservationDurationSeconds = conservationDurationSeconds;
        ConservationTargetCpFraction = conservationTargetCpFraction;
        RecoveryDurationSeconds = recoveryDurationSeconds;
        RecoveryTargetCpFraction = recoveryTargetCpFraction;
        IncludeFatigueReport = includeFatigueReport;
        EnableRefinement = enableRefinement;
    }

    public double? CriticalPowerWatts { get; }
    public double? WPrimeJoules { get; }
    public IReadOnlyList<MatchBurnWindow> Windows { get; }
    public double ConservationDurationSeconds { get; }
    public double ConservationTargetCpFraction { get; }
    public double RecoveryDurationSeconds { get; }
    public double RecoveryTargetCpFraction { get; }
    public bool IncludeFatigueReport { get; }
    public bool EnableRefinement { get; }
}

public sealed record MatchBurnWindowReport(int WindowIndex, int MatchedSegmentCount);
public sealed record MatchPhaseReport(MatchPhase Phase, int SegmentCount, double MovingSeconds);

public sealed record MatchBurningReport(
    double CriticalPowerWatts,
    CapacityProvenance CriticalPowerProvenance,
    double WPrimeJoules,
    CapacityProvenance WPrimeProvenance,
    IReadOnlyList<MatchBurnWindowReport> Windows,
    IReadOnlyList<MatchPhaseReport> Phases,
    double MinimumWPrimeBalanceJoules,
    double FinalWPrimeBalanceJoules,
    double DepletedFraction,
    double TimeAboveCriticalPowerSeconds,
    double WorkAboveCriticalPowerJoules,
    IReadOnlyList<int> CriticalSequences,
    int? FirstInfeasibleSequence,
    MatchBurningVerdict Verdict,
    bool RefinementEnabled,
    bool RefinementRan,
    bool RefinementChangedAssignments,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.VariableMatchBurning);
