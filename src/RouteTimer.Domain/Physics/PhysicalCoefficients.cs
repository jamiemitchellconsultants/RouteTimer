namespace RouteTimer.Domain.Physics;

public sealed record PhysicalCoefficients(double DrivetrainEfficiency, double AirDensity, double Crr, double CdA)
{
    public static PhysicalCoefficients Default { get; } = new(.97, 1.225, .005, .32);
}
