using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Tests.Routes;

namespace RouteTimer.Services.Tests.Predictions;

internal static class PredictionFixtures
{
    public static RouteTimer.Domain.Predictions.PredictionResult PredictStraightRoute()
    {
        var points = RouteFixtures.StraightClimb(200, 10, 0).Select(point => new GeoPoint(point.Latitude, point.Longitude, point.ElevationMetres)).ToList();
        var route = new RouteProcessor(RouteProcessingOptions.Default).Process(points);
        var power = new PowerModel([new PowerBand("-1:1", "0:30", 220, TimeSpan.FromMinutes(30), 3, 1, ConfidenceLevel.High)], 220);
        var rider = new RiderModel(power, PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1");
        return new RoutePredictor().Predict(route, new RiderProfile(75, 10), rider);
    }
}
