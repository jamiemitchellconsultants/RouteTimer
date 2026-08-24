using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Models;

/// <summary>
/// Query side of the personal power model: given a gradient and elapsed moving duration, bilinearly
/// interpolates a wattage estimate across the gradient x duration band grid (see
/// <see cref="PowerModelBands"/>). The model it's handed may be sparse (built by an older builder
/// version, or hand-constructed in tests), so every lookup is defensive about missing cells rather
/// than assuming a dense 40-cell grid.
/// </summary>
public sealed class PowerLookup
{
    private readonly PowerModel _model;
    private readonly Dictionary<(string GradeKey, string DurationKey), PowerBand> _exact;
    private readonly ILookup<string, PowerBand> _byGradeKey;
    private readonly ILookup<string, PowerBand> _byDurationKey;
    private readonly Dictionary<string, DurationBand> _durationBandsByKey;
    private readonly Dictionary<string, GradientBand> _gradientBandsByKey;

    public PowerLookup(PowerModel model)
    {
        _model = model;
        _exact = model.Bands.ToDictionary(band => (band.GradeKey, band.DurationKey));
        _byGradeKey = model.Bands.ToLookup(band => band.GradeKey);
        _byDurationKey = model.Bands.ToLookup(band => band.DurationKey);
        _durationBandsByKey = PowerModelBands.Duration.ToDictionary(band => band.Key);
        _gradientBandsByKey = PowerModelBands.Gradient.ToDictionary(band => band.Key);
    }

    public PowerEstimate GetWatts(double gradient, TimeSpan elapsed)
    {
        if (_model.Bands.Count == 0) return new PowerEstimate(_model.GlobalTypicalWatts, ConfidenceLevel.Low, true, "no-band-evidence");

        var (gradeLower, gradeUpper, gradeExtrapolated) =
            Bracket(PowerModelBands.Gradient, gradient, band => band.Anchor, band => band.LowerBound is null, band => band.UpperBound is null);
        var (durationLower, durationUpper, durationExtrapolated) =
            Bracket(PowerModelBands.Duration, elapsed, band => band.Anchor, band => band.LowerBound is null, band => band.UpperBound is null);

        var corners = new Dictionary<(string GradeKey, string DurationKey), (double Watts, ConfidenceLevel Confidence, bool WasFallback)>();
        (double Watts, ConfidenceLevel Confidence, bool WasFallback) Corner(GradientBand gradeBand, DurationBand durationBand)
        {
            var key = (gradeBand.Key, durationBand.Key);
            if (!corners.TryGetValue(key, out var resolved))
            {
                resolved = ResolveCorner(gradeBand, durationBand);
                corners[key] = resolved;
            }

            return resolved;
        }

        var lowGradeLowDuration = Corner(gradeLower, durationLower);
        var lowGradeHighDuration = Corner(gradeLower, durationUpper);
        var highGradeLowDuration = Corner(gradeUpper, durationLower);
        var highGradeHighDuration = Corner(gradeUpper, durationUpper);

        var atLowGrade = BlendByDuration(lowGradeLowDuration.Watts, lowGradeHighDuration.Watts, durationLower, durationUpper, elapsed);
        var atHighGrade = BlendByDuration(highGradeLowDuration.Watts, highGradeHighDuration.Watts, durationLower, durationUpper, elapsed);
        var watts = BlendByGradient(atLowGrade, atHighGrade, gradeLower, gradeUpper, gradient);

        var confidence = corners.Values.Select(corner => corner.Confidence).Min();
        var extrapolated = gradeExtrapolated || durationExtrapolated || corners.Values.Any(corner => corner.WasFallback);

        return new PowerEstimate(Math.Max(0, watts), confidence, extrapolated, extrapolated ? "nearest-band" : "interpolated");
    }

    /// <summary>
    /// Brackets <paramref name="target"/> between the two adjacent bands whose anchors straddle it
    /// (collapsing to a single band at either grid edge), and reports whether the query falls beyond
    /// the grid's outermost anchor on a side that's genuinely open-ended (per the corrected rule: a
    /// closed-but-off-centre first/last band, like duration's "0:30" with LowerBound Zero, is not
    /// extrapolation - it simply has no data below it).
    /// </summary>
    private static (T Lower, T Upper, bool Extrapolated) Bracket<T, TKey>(
        IReadOnlyList<T> bands,
        TKey target,
        Func<T, TKey> anchorOf,
        Func<T, bool> isOpenEndedLow,
        Func<T, bool> isOpenEndedHigh)
        where TKey : IComparable<TKey>
    {
        var lower = bands.LastOrDefault(band => anchorOf(band).CompareTo(target) <= 0) ?? bands[0];
        var upper = bands.FirstOrDefault(band => anchorOf(band).CompareTo(target) >= 0) ?? bands[^1];

        var first = bands[0];
        var last = bands[^1];
        var extrapolatedLow = target.CompareTo(anchorOf(first)) < 0 && isOpenEndedLow(first);
        var extrapolatedHigh = target.CompareTo(anchorOf(last)) > 0 && isOpenEndedHigh(last);

        return (lower, upper, extrapolatedLow || extrapolatedHigh);
    }

    /// <summary>
    /// Resolves an effective (Watts, Confidence, WasFallback) for one grid corner from the (possibly
    /// sparse) model, in priority order: exact cell, same-gradient/nearest-duration, same-duration/
    /// nearest-gradient, then GlobalTypicalWatts as the last resort.
    /// </summary>
    private (double Watts, ConfidenceLevel Confidence, bool WasFallback) ResolveCorner(GradientBand gradeBand, DurationBand durationBand)
    {
        if (_exact.TryGetValue((gradeBand.Key, durationBand.Key), out var exact)) return (exact.TypicalWatts, exact.Confidence, false);

        var sameGrade = _byGradeKey[gradeBand.Key].ToList();
        if (sameGrade.Count > 0)
        {
            var nearest = sameGrade.MinBy(band => Math.Abs((_durationBandsByKey[band.DurationKey].Anchor - durationBand.Anchor).Ticks));
            return (nearest!.TypicalWatts, nearest.Confidence, true);
        }

        var sameDuration = _byDurationKey[durationBand.Key].ToList();
        if (sameDuration.Count > 0)
        {
            var nearest = sameDuration.MinBy(band => Math.Abs(_gradientBandsByKey[band.GradeKey].Anchor - gradeBand.Anchor));
            return (nearest!.TypicalWatts, nearest.Confidence, true);
        }

        return (_model.GlobalTypicalWatts, ConfidenceLevel.Low, true);
    }

    /// <summary>Linear blend along the duration axis, guarding the zero-span (single-band) case.</summary>
    private static double BlendByDuration(double lowerWatts, double upperWatts, DurationBand lower, DurationBand upper, TimeSpan elapsed)
    {
        var span = (upper.Anchor - lower.Anchor).Ticks;
        if (span == 0) return lowerWatts;
        return lowerWatts + ((upperWatts - lowerWatts) * ((elapsed - lower.Anchor).Ticks / (double)span));
    }

    /// <summary>Linear blend along the gradient axis, guarding the zero-span (single-band) case.</summary>
    private static double BlendByGradient(double lowerWatts, double upperWatts, GradientBand lower, GradientBand upper, double gradient)
    {
        var span = upper.Anchor - lower.Anchor;
        if (span == 0) return lowerWatts;
        return lowerWatts + ((upperWatts - lowerWatts) * ((gradient - lower.Anchor) / span));
    }
}
