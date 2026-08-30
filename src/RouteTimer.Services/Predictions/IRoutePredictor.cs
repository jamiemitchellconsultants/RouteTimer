using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Predictions;

public interface IRoutePredictor
{
    PredictionResult Predict(
        PredictionRoute route,
        RiderProfile profile,
        RiderModel model,
        CancellationToken cancellationToken = default);
}
