namespace RouteTimer.Domain.Models;

/// <summary>
/// One gradient band. Bounds are fractions (0.05 = 5%), matching CleanRideSample.Gradient's units.
/// Null LowerBound/UpperBound means the band is open-ended in that direction.
/// Anchor is the representative value used for interpolation/extrapolation math: the midpoint for a
/// closed band, or the single finite bound for an open-ended band (so a query beyond the outermost
/// anchor collapses to that band's own value directly, matching "extrapolation uses the nearest
/// supported value" rather than extrapolating a slope past the edge of the data).
/// </summary>
public sealed record GradientBand(string Key, double? LowerBound, double? UpperBound, double Anchor)
{
    public bool Contains(double gradient) => (LowerBound is null || gradient >= LowerBound) && (UpperBound is null || gradient < UpperBound);
}

/// <summary>Same idea as <see cref="GradientBand"/>, for elapsed moving-duration bands.</summary>
public sealed record DurationBand(string Key, TimeSpan? LowerBound, TimeSpan? UpperBound, TimeSpan Anchor)
{
    public bool Contains(TimeSpan elapsed) => (LowerBound is null || elapsed >= LowerBound) && (UpperBound is null || elapsed < UpperBound);
}

/// <summary>
/// Single source of truth for the personal power model's gradient x duration band grid.
/// Both the model builder (writer) and the power lookup (reader) depend on these definitions
/// so the boundaries and keys never drift apart between the two sides.
/// </summary>
public static class PowerModelBands
{
    // Grade-key labels use percent notation for readability/persistence (matching the design spec's
    // own prose), but the numeric Lower/Upper/Anchor fields are fractions to match CleanRideSample.Gradient.
    public static IReadOnlyList<GradientBand> Gradient { get; } =
    [
        new("-100:-6", null, -.06, -.06),
        new("-6:-3", -.06, -.03, -.045),
        new("-3:-1", -.03, -.01, -.02),
        new("-1:1", -.01, .01, 0),
        new("1:3", .01, .03, .02),
        new("3:6", .03, .06, .045),
        new("6:9", .06, .09, .075),
        new("9:100", .09, null, .09),
    ];

    public static IReadOnlyList<DurationBand> Duration { get; } =
    [
        new("0:30", TimeSpan.Zero, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(15)),
        new("30:60", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(45)),
        new("60:120", TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(90)),
        new("120:180", TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(180), TimeSpan.FromMinutes(150)),
        new("180:+", TimeSpan.FromMinutes(180), null, TimeSpan.FromMinutes(180)),
    ];

    public static GradientBand FindGradientBand(double gradient) =>
        Gradient.FirstOrDefault(band => band.Contains(gradient)) ?? (gradient < 0 ? Gradient[0] : Gradient[^1]);

    public static DurationBand FindDurationBand(TimeSpan elapsed) =>
        Duration.FirstOrDefault(band => band.Contains(elapsed)) ?? Duration[^1];
}
