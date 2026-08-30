using RouteTimer.Domain.Adjustments.SegmentGains;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Predictions;

namespace RouteTimer.Services.Adjustments.SegmentGains;

/// <summary>
/// Per-run <see cref="IPowerTargetPolicy"/> that applies a segment-gains definition's rules in
/// submitted order, first match wins, while an unmatched segment keeps its baseline power unchanged.
/// One instance is used for exactly one <see cref="RoutePredictor"/> replay, so its counters describe
/// that single run.
/// </summary>
public sealed class SegmentGainsPolicy : IPowerTargetPolicy
{
    private readonly IReadOnlyList<SegmentGainsRule> _rules;
    private readonly int[] _hitCounts;

    public SegmentGainsPolicy(IReadOnlyList<SegmentGainsRule> rules)
    {
        _rules = rules;
        _hitCounts = new int[rules.Count];
    }

    public int MatchedSegmentCount { get; private set; }
    public int UnmatchedSegmentCount { get; private set; }
    public bool AnyClamped { get; private set; }
    public IReadOnlyList<int> HitCounts => _hitCounts;

    public PowerEstimate Resolve(PowerTargetContext context)
    {
        for (var index = 0; index < _rules.Count; index++)
        {
            if (!_rules[index].Matches(context.Segment)) continue;

            var (watts, clamped) = _rules[index].Apply(context.BaselineEstimate.Watts);
            _hitCounts[index]++;
            MatchedSegmentCount++;
            if (clamped) AnyClamped = true;
            return context.BaselineEstimate with { Watts = watts };
        }

        UnmatchedSegmentCount++;
        return context.BaselineEstimate;
    }
}
