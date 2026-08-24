namespace RouteTimer.Contracts.Predictions;

public sealed record PredictionRoutePreview(string Name, double DistanceMetres, double AscentMetres, int SampleCount);
