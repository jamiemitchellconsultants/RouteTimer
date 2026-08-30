using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteTimer.Domain.Adjustments.Zones;

public enum ZoneThresholdMode { FtpBased, ModelInferred }
public enum ZonePlacement { LowerBound, Midpoint, UpperBound }
public enum ZoneThresholdProvenance { SuppliedFtp, InferredModel }

public sealed record ZoneAssignment
{
    public ZoneAssignment(bool allSegments, double? minGradient, double? maxGradient, int zone, ZonePlacement placement)
    {
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentException("Invalid zone placement value.", nameof(placement));
        }

        if (allSegments)
        {
            if (minGradient is not null || maxGradient is not null)
            {
                throw new ArgumentException("All-segments assignment must have null gradient bounds.");
            }
        }
        else
        {
            if (minGradient is null && maxGradient is null)
            {
                throw new ArgumentException("Gradient assignment must specify at least one bound.");
            }

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

        AllSegments = allSegments;
        MinGradient = minGradient;
        MaxGradient = maxGradient;
        Zone = zone;
        Placement = placement;
    }

    public bool AllSegments { get; }
    public double? MinGradient { get; }
    public double? MaxGradient { get; }
    public int Zone { get; }
    public ZonePlacement Placement { get; }

    public bool Matches(double gradient)
    {
        if (AllSegments) return true;
        return (MinGradient is null || gradient >= MinGradient.Value) &&
               (MaxGradient is null || gradient <= MaxGradient.Value);
    }
}

public sealed record ZoneShiftDefinition : PacingStrategyDefinition
{
    public const int MaximumAssignments = 10;

    public ZoneShiftDefinition(ZoneThresholdMode thresholdMode, double? ftpWatts, IReadOnlyList<ZoneAssignment> assignments)
        : base(PacingStrategyType.RpeZoneShift)
    {
        if (!Enum.IsDefined(thresholdMode))
        {
            throw new ArgumentException("Invalid threshold mode value.", nameof(thresholdMode));
        }

        if (thresholdMode == ZoneThresholdMode.FtpBased)
        {
            if (ftpWatts is null || double.IsNaN(ftpWatts.Value) || double.IsInfinity(ftpWatts.Value) || ftpWatts.Value < 1 || ftpWatts.Value > 2000)
            {
                throw new ArgumentException("FTP watts must be between 1 and 2000 in FtpBased mode.", nameof(ftpWatts));
            }
        }
        else if (thresholdMode == ZoneThresholdMode.ModelInferred)
        {
            if (ftpWatts is not null)
            {
                throw new ArgumentException("FTP watts must be null in ModelInferred mode.", nameof(ftpWatts));
            }
        }

        if (assignments is null || assignments.Count == 0 || assignments.Count > MaximumAssignments)
        {
            throw new ArgumentException($"Assignments list must contain between 1 and {MaximumAssignments} items.", nameof(assignments));
        }

        if (assignments.Any(a => a is null))
        {
            throw new ArgumentException("Assignments list cannot contain null items.", nameof(assignments));
        }

        int maxZone = thresholdMode == ZoneThresholdMode.FtpBased ? 7 : 5;
        foreach (var a in assignments)
        {
            if (a.Zone < 1 || a.Zone > maxZone)
            {
                throw new ArgumentException($"Zone number must be in range [1, {maxZone}] for {thresholdMode} mode.", nameof(assignments));
            }
        }

        int fallbackCount = assignments.Count(a => a.AllSegments);
        if (fallbackCount > 1)
        {
            throw new ArgumentException("At most one assignment can have AllSegments = true.", nameof(assignments));
        }

        ThresholdMode = thresholdMode;
        FtpWatts = ftpWatts;

        Assignments = assignments.Where(a => !a.AllSegments).Concat(assignments.Where(a => a.AllSegments)).ToList();
    }

    public ZoneThresholdMode ThresholdMode { get; }
    public double? FtpWatts { get; }
    public IReadOnlyList<ZoneAssignment> Assignments { get; }
}

public sealed record ResolvedPowerZone(int Zone, double LowerWatts, double UpperWatts, double LowerTargetWatts, double MidpointTargetWatts, double UpperTargetWatts);

public sealed record ResolvedPowerZoneSet(
    double ThresholdWatts,
    ZoneThresholdProvenance Provenance,
    IReadOnlyList<ResolvedPowerZone> Zones);

public sealed record ZoneDistributionEntry(int Zone, double MovingSeconds, double Percentage);

public sealed record ZoneShiftReport(
    double ResolvedThresholdWatts,
    ZoneThresholdProvenance Provenance,
    IReadOnlyList<ResolvedPowerZone> Boundaries,
    IReadOnlyList<int> AssignmentMatchCounts,
    IReadOnlyList<ZoneDistributionEntry> Distribution,
    double AveragePowerWatts,
    double NormalizedPowerWatts,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts)
    : PacingStrategyReport(PacingStrategyType.RpeZoneShift);
