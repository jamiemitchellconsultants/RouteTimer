using RouteTimer.Domain.Physics;

namespace RouteTimer.Domain.Models;

public sealed record RiderModel(PowerModel PowerModel, PhysicalCoefficients Coefficients, string AlgorithmVersion);
