using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Models;
using RouteTimer.Services.Physics;

namespace RouteTimer.Services.Predictions;

public sealed class RoutePredictor : IRoutePredictor
{
    public PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);
        var lookup = new PowerLookup(model.PowerModel);
        var segments = new List<PredictionSegment>();
        var elapsed = TimeSpan.Zero;
        var mass = profile.RiderWeightKg + profile.BikeAndEquipmentWeightKg;

        foreach (var sample in route.Samples.Skip(1))
        {
            var estimate = lookup.GetWatts(sample.Gradient, elapsed);
            var speed = SolveSpeed(sample.Gradient, estimate.Watts, mass, model);
            var seconds = sample.SegmentDistanceMetres / speed;
            if (!double.IsFinite(seconds) || seconds <= 0) throw new PredictionCalculationException("Prediction could not advance along the route.");
            var duration = TimeSpan.FromSeconds(seconds);
            elapsed += duration;
            segments.Add(new PredictionSegment(sample.Sequence, sample.SegmentDistanceMetres, sample.Gradient, estimate.Watts, speed, duration, estimate.Confidence));
        }

        var confidence = segments.Count > 0 && segments.All(segment => segment.Confidence == ConfidenceLevel.High) ? ConfidenceLevel.High : ConfidenceLevel.Low;
        return new PredictionResult(segments, elapsed, confidence);
    }

    private static double SolveSpeed(double grade, double watts, double mass, RiderModel model)
    {
        var low = .5;
        var high = 20d;
        for (var iteration = 0; iteration < 48; iteration++)
        {
            var middle = (low + high) / 2;
            if (CyclingForces.RequiredRiderPower(grade, middle, mass, model.Coefficients) > watts) high = middle;
            else low = middle;
        }

        return Math.Min(20, Math.Max(.5, low));
    }
}
