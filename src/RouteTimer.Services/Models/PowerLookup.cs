using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Models;

public sealed class PowerLookup(PowerModel model)
{
    public PowerEstimate GetWatts(double gradient, TimeSpan elapsed)
    {
        var bands = model.Bands.Where(band => band.DurationKey == "0:30").OrderBy(band => GradeCentre(band.GradeKey)).ToList();
        if (bands.Count == 0) return new PowerEstimate(model.GlobalTypicalWatts, ConfidenceLevel.Low, true, "no-band-evidence");
        var target = gradient * 100;
        var lower = bands.LastOrDefault(band => GradeCentre(band.GradeKey) <= target) ?? bands[0];
        var upper = bands.FirstOrDefault(band => GradeCentre(band.GradeKey) >= target) ?? bands[^1];
        var extrapolated = target < GradeCentre(bands[0].GradeKey) || target > GradeCentre(bands[^1].GradeKey);
        var span = GradeCentre(upper.GradeKey) - GradeCentre(lower.GradeKey);
        var watts = span == 0 ? lower.TypicalWatts : lower.TypicalWatts + ((upper.TypicalWatts - lower.TypicalWatts) * ((target - GradeCentre(lower.GradeKey)) / span));
        return new PowerEstimate(Math.Max(0, watts), lower.Confidence < upper.Confidence ? lower.Confidence : upper.Confidence, extrapolated, extrapolated ? "nearest-band" : "interpolated");
    }
    private static double GradeCentre(string key) => key.Split(':').Select(double.Parse).Average();
}
