using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;
using RouteTimer.Domain.Routes;

namespace RouteTimer.Services.Predictions;

public interface IRoutePredictor
{
    PredictionResult Predict(ProcessedRoute route, RiderProfile profile, RiderModel model);
}
