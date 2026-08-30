using RouteTimer.Domain.Predictions;

namespace RouteTimer.Domain.Adjustments.SegmentGains;

public enum SegmentGainsSelector { Gradient, Sequence, Distance }

/// <summary>
/// One power-adjustment rule matched against a single route segment. Exactly one selector
/// (gradient, sequence, or cumulative distance) may be bounded per rule, and exactly one of
/// <see cref="Factor"/>/<see cref="DeltaWatts"/> determines how a matched segment's power changes.
/// Bounds are inclusive; an unset bound is open on that side.
/// </summary>
public sealed record SegmentGainsRule
{
    public double? MinGradient { get; }
    public double? MaxGradient { get; }
    public int? MinSequence { get; }
    public int? MaxSequence { get; }
    public double? MinCumulativeDistanceMetres { get; }
    public double? MaxCumulativeDistanceMetres { get; }
    public double? Factor { get; }
    public double? DeltaWatts { get; }
    public SegmentGainsSelector Selector { get; }

    public SegmentGainsRule(
        double? minGradient, double? maxGradient,
        int? minSequence, int? maxSequence,
        double? minCumulativeDistanceMetres, double? maxCumulativeDistanceMetres,
        double? factor, double? deltaWatts)
    {
        var gradientSet = minGradient is not null || maxGradient is not null;
        var sequenceSet = minSequence is not null || maxSequence is not null;
        var distanceSet = minCumulativeDistanceMetres is not null || maxCumulativeDistanceMetres is not null;
        var selectorCount = (gradientSet ? 1 : 0) + (sequenceSet ? 1 : 0) + (distanceSet ? 1 : 0);
        if (selectorCount != 1)
            throw new ArgumentException("A segment gains rule must bound exactly one of gradient, sequence, or distance.");

        if (minGradient is not null && (!double.IsFinite(minGradient.Value)) || maxGradient is not null && !double.IsFinite(maxGradient.Value))
            throw new ArgumentException("A segment gains rule's gradient bounds must be finite.");
        if (minGradient is not null && maxGradient is not null && minGradient > maxGradient)
            throw new ArgumentException("A segment gains rule's minimum gradient must not exceed its maximum.");
        if (minSequence is not null && maxSequence is not null && minSequence > maxSequence)
            throw new ArgumentException("A segment gains rule's minimum sequence must not exceed its maximum.");
        if (minCumulativeDistanceMetres is not null && (!double.IsFinite(minCumulativeDistanceMetres.Value) || minCumulativeDistanceMetres < 0) ||
            maxCumulativeDistanceMetres is not null && (!double.IsFinite(maxCumulativeDistanceMetres.Value) || maxCumulativeDistanceMetres < 0))
            throw new ArgumentException("A segment gains rule's distance bounds must be finite and non-negative.");
        if (minCumulativeDistanceMetres is not null && maxCumulativeDistanceMetres is not null && minCumulativeDistanceMetres > maxCumulativeDistanceMetres)
            throw new ArgumentException("A segment gains rule's minimum distance must not exceed its maximum.");

        if ((factor is null) == (deltaWatts is null))
            throw new ArgumentException("A segment gains rule requires exactly one of factor or delta watts.");
        if (factor is not null && (!double.IsFinite(factor.Value) || factor.Value <= 0))
            throw new ArgumentException("A segment gains rule's factor must be a positive finite number.");
        if (deltaWatts is not null && !double.IsFinite(deltaWatts.Value))
            throw new ArgumentException("A segment gains rule's delta watts must be finite.");

        MinGradient = minGradient;
        MaxGradient = maxGradient;
        MinSequence = minSequence;
        MaxSequence = maxSequence;
        MinCumulativeDistanceMetres = minCumulativeDistanceMetres;
        MaxCumulativeDistanceMetres = maxCumulativeDistanceMetres;
        Factor = factor;
        DeltaWatts = deltaWatts;
        Selector = gradientSet ? SegmentGainsSelector.Gradient : sequenceSet ? SegmentGainsSelector.Sequence : SegmentGainsSelector.Distance;
    }

    public bool Matches(PredictionRouteSegment segment) => Selector switch
    {
        SegmentGainsSelector.Gradient =>
            (MinGradient is null || segment.Gradient >= MinGradient.Value) && (MaxGradient is null || segment.Gradient <= MaxGradient.Value),
        SegmentGainsSelector.Sequence =>
            (MinSequence is null || segment.Sequence >= MinSequence.Value) && (MaxSequence is null || segment.Sequence <= MaxSequence.Value),
        SegmentGainsSelector.Distance =>
            (MinCumulativeDistanceMetres is null || segment.CumulativeDistanceMetres >= MinCumulativeDistanceMetres.Value) &&
            (MaxCumulativeDistanceMetres is null || segment.CumulativeDistanceMetres <= MaxCumulativeDistanceMetres.Value),
        _ => throw new InvalidOperationException($"Unhandled segment gains selector {Selector}."),
    };

    /// <summary>Applies this rule's factor/delta to a baseline watts value, flooring the result at 10 W.</summary>
    public (double Watts, bool Clamped) Apply(double baselineWatts)
    {
        var raw = Factor is { } factor ? baselineWatts * factor : baselineWatts + DeltaWatts!.Value;
        var watts = Math.Max(10, raw);
        return (watts, watts > raw);
    }
}

/// <summary>
/// Segment-specific gains: an ordered list of up to <see cref="MaximumRules"/> rules, each matched
/// against route segments in submitted order with first-match-wins precedence. A segment matching no
/// rule keeps its baseline power unchanged.
/// </summary>
public sealed record SegmentGainsDefinition : PacingStrategyDefinition
{
    public const int MaximumRules = 10;

    private readonly IReadOnlyList<SegmentGainsRule> _rules;

    public SegmentGainsDefinition(IReadOnlyList<SegmentGainsRule> rules) : base(PacingStrategyType.SegmentSpecificGains)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Any(rule => rule is null))
            throw new ArgumentException("Segment gains rules must not be null.", nameof(rules));
        if (rules.Count > MaximumRules)
            throw new ArgumentException($"Segment-specific gains supports at most {MaximumRules} rules.", nameof(rules));

        _rules = Array.AsReadOnly(rules.ToArray());
    }

    public IReadOnlyList<SegmentGainsRule> Rules => _rules;
}
