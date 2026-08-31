using System;
using System.Collections.Generic;
using RouteTimer.Domain.Adjustments.MatchBurning;
using RouteTimer.Domain.Predictions;

namespace RouteTimer.Services.Adjustments.MatchBurning;

public sealed record WPrimeBalancePoint(
    int Sequence,
    double DisplayBalanceJoules,
    bool Infeasible);

public sealed record WPrimeBalanceResult(
    IReadOnlyList<WPrimeBalancePoint> Points,
    double MinimumBalanceJoules,
    double FinalBalanceJoules,
    double TimeAboveCriticalPowerSeconds,
    double WorkAboveCriticalPowerJoules,
    int? FirstInfeasibleSequence,
    MatchBurningVerdict Verdict);

public static class WPrimeBalanceCalculator
{
    public static WPrimeBalanceResult Calculate(
        IReadOnlyList<PredictionSegment> segments,
        double criticalPowerWatts,
        double wPrimeJoules)
    {
        if (segments is null) throw new ArgumentNullException(nameof(segments));
        if (double.IsNaN(criticalPowerWatts) || double.IsInfinity(criticalPowerWatts) || criticalPowerWatts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(criticalPowerWatts), "CP must be positive and finite.");
        }
        if (double.IsNaN(wPrimeJoules) || double.IsInfinity(wPrimeJoules) || wPrimeJoules <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wPrimeJoules), "W-prime must be positive and finite.");
        }

        var points = new List<WPrimeBalancePoint>();
        double rawBalance = wPrimeJoules;
        double minDisplayBalance = wPrimeJoules;
        double timeAboveCp = 0;
        double workAboveCp = 0;
        int? firstInfeasibleSeq = null;

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            double p = seg.PowerWatts;
            double d = seg.MovingTime.TotalSeconds;

            if (double.IsNaN(p) || double.IsInfinity(p) || p < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segments), "Segment power must be non-negative finite.");
            }
            if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segments), "Segment duration must be positive finite.");
            }

            if (p > criticalPowerWatts)
            {
                double exp = (p - criticalPowerWatts) * d;
                rawBalance -= exp;
                timeAboveCp += d;
                workAboveCp += exp;
            }
            else if (p < criticalPowerWatts)
            {
                double dcp = criticalPowerWatts - p;
                double tau = 546.0 * Math.Exp(-0.01 * dcp) + 316.0;
                rawBalance = wPrimeJoules - (wPrimeJoules - rawBalance) * Math.Exp(-d / tau);
                if (rawBalance > wPrimeJoules)
                {
                    rawBalance = wPrimeJoules;
                }
            }

            if (rawBalance <= 0 && firstInfeasibleSeq is null)
            {
                firstInfeasibleSeq = seg.Sequence;
            }

            // Latched deliberately: the raw balance keeps integrating underneath and may recover, but
            // once a rider has run out mid-effort the rest of the plan is not something they could have
            // ridden, so the displayed trace stays at zero rather than implying a recovery that never
            // happened.
            bool isInfeasible = firstInfeasibleSeq is not null;
            double displayBalance = isInfeasible ? 0.0 : Math.Clamp(rawBalance, 0.0, wPrimeJoules);

            if (displayBalance < minDisplayBalance)
            {
                minDisplayBalance = displayBalance;
            }

            points.Add(new WPrimeBalancePoint(seg.Sequence, displayBalance, isInfeasible));
        }

        double finalDisplayBalance = points.Count > 0 ? points[^1].DisplayBalanceJoules : wPrimeJoules;
        double minRatio = minDisplayBalance / wPrimeJoules;

        MatchBurningVerdict verdict;
        if (firstInfeasibleSeq is not null || minRatio <= 0)
        {
            verdict = MatchBurningVerdict.Infeasible;
        }
        else if (minRatio < 0.10)
        {
            verdict = MatchBurningVerdict.Risky;
        }
        else if (minRatio < 0.30)
        {
            verdict = MatchBurningVerdict.Aggressive;
        }
        else
        {
            verdict = MatchBurningVerdict.Manageable;
        }

        return new WPrimeBalanceResult(
            points,
            minDisplayBalance,
            finalDisplayBalance,
            timeAboveCp,
            workAboveCp,
            firstInfeasibleSeq,
            verdict);
    }
}
