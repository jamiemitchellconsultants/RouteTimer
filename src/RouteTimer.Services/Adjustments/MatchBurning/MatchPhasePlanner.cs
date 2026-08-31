using System;
using System.Collections.Generic;
using System.Linq;
using RouteTimer.Domain.Adjustments.MatchBurning;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;

namespace RouteTimer.Services.Adjustments.MatchBurning;

public sealed record MatchPhaseAssignment(int Sequence, MatchPhase Phase, int? BurnWindowIndex);

/// <summary>
/// <paramref name="WindowMatchCounts"/> counts every segment each window matched, so with overlapping
/// windows the counts sum to more than the number of burn segments - a segment matched by two windows is
/// counted by both, while its intensity comes from the first (see
/// <paramref name="HasOverlappingBurnWindows"/>, which raises the overlap warning).
/// </summary>
public sealed record MatchPhasePlan(
    IReadOnlyDictionary<int, MatchPhaseAssignment> BySequence,
    IReadOnlyList<int> WindowMatchCounts,
    bool HasOverlappingBurnWindows);

public static class MatchPhasePlanner
{
    public static MatchPhasePlan Plan(
        PredictionRoute route,
        PredictionResult timing,
        MatchBurningDefinition definition)
    {
        if (route is null) throw new ArgumentNullException(nameof(route));
        if (timing is null) throw new ArgumentNullException(nameof(timing));
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        if (route.Segments.Count != timing.Segments.Count)
        {
            throw new ArgumentException("Route and timing segment counts must match.");
        }

        int count = route.Segments.Count;
        for (int i = 0; i < count; i++)
        {
            if (route.Segments[i].Sequence != timing.Segments[i].Sequence)
            {
                throw new ArgumentException("Route and timing segment sequences must match.");
            }
        }

        var windowMatchCounts = new int[definition.Windows.Count];
        var matchingWindowIndices = new List<int>[count];
        bool hasOverlapping = false;

        for (int i = 0; i < count; i++)
        {
            matchingWindowIndices[i] = new List<int>();
            var routeSeg = route.Segments[i];

            for (int w = 0; w < definition.Windows.Count; w++)
            {
                if (definition.Windows[w].Matches(routeSeg))
                {
                    windowMatchCounts[w]++;
                    matchingWindowIndices[i].Add(w);
                }
            }

            if (matchingWindowIndices[i].Count > 1)
            {
                hasOverlapping = true;
            }
        }

        var phases = new MatchPhase[count];
        var winningBurnWindowIndex = new int?[count];

        for (int i = 0; i < count; i++)
        {
            if (matchingWindowIndices[i].Count > 0)
            {
                phases[i] = MatchPhase.Burn;
                winningBurnWindowIndex[i] = matchingWindowIndices[i][0];
            }
            else
            {
                phases[i] = MatchPhase.Baseline;
            }
        }

        var isBurn = phases.Select(p => p == MatchPhase.Burn).ToArray();
        var isConservationCandidate = new bool[count];
        var isRecoveryCandidate = new bool[count];

        int index = 0;
        while (index < count)
        {
            if (isBurn[index])
            {
                int blockStart = index;
                while (index < count && isBurn[index])
                {
                    index++;
                }
                int blockEnd = index - 1;

                if (definition.ConservationDurationSeconds > 0)
                {
                    // Stop at the previous burn block rather than walking through it: its segments are
                    // not conservation candidates, and charging their duration to this block's budget
                    // would silently shorten the conservation run-in.
                    double accSeconds = 0;
                    int walk = blockStart - 1;
                    while (walk >= 0 && !isBurn[walk] && accSeconds < definition.ConservationDurationSeconds)
                    {
                        isConservationCandidate[walk] = true;
                        accSeconds += timing.Segments[walk].MovingTime.TotalSeconds;
                        walk--;
                    }
                }

                if (definition.RecoveryDurationSeconds > 0)
                {
                    // Same for the recovery run-out: the next burn block ends it.
                    double accSeconds = 0;
                    int walk = blockEnd + 1;
                    while (walk < count && !isBurn[walk] && accSeconds < definition.RecoveryDurationSeconds)
                    {
                        isRecoveryCandidate[walk] = true;
                        accSeconds += timing.Segments[walk].MovingTime.TotalSeconds;
                        walk++;
                    }
                }
            }
            else
            {
                index++;
            }
        }

        var dict = new Dictionary<int, MatchPhaseAssignment>();
        for (int i = 0; i < count; i++)
        {
            int seq = route.Segments[i].Sequence;
            MatchPhase finalPhase;

            if (isBurn[i])
            {
                finalPhase = MatchPhase.Burn;
            }
            else if (isRecoveryCandidate[i])
            {
                finalPhase = MatchPhase.Recovery;
            }
            else if (isConservationCandidate[i])
            {
                finalPhase = MatchPhase.Conservation;
            }
            else
            {
                finalPhase = MatchPhase.Baseline;
            }

            dict[seq] = new MatchPhaseAssignment(seq, finalPhase, winningBurnWindowIndex[i]);
        }

        return new MatchPhasePlan(dict, windowMatchCounts, hasOverlapping);
    }
}
