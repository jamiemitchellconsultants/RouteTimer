using RouteTimer.Domain.Physics;

namespace RouteTimer.Services.Physics;

public sealed record PhysicalCalibrationResult(
    PhysicalCoefficients Coefficients,
    bool WasCalibrated,
    string ReasonCode);
