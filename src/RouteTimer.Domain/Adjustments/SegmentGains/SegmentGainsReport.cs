namespace RouteTimer.Domain.Adjustments.SegmentGains;

/// <summary>How many segments matched one rule, indexed by that rule's position in the submitted order.</summary>
public sealed record SegmentGainsRuleHitCount(int RuleIndex, int SegmentCount);

public sealed record SegmentGainsReport(
    int MatchedSegmentCount,
    int UnmatchedSegmentCount,
    IReadOnlyList<SegmentGainsRuleHitCount> RuleHitCounts,
    double MovingTimeDeltaSeconds,
    double AverageSpeedDeltaMetresPerSecond,
    double AveragePowerDeltaWatts) : PacingStrategyReport(PacingStrategyType.SegmentSpecificGains);
