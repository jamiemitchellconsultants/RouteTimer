using RouteTimer.Domain.Physics;

namespace RouteTimer.Services.Physics;

public static class CyclingForces
{
    private const double Gravity = 9.80665;

    public static double RequiredRiderPower(double grade, double speedMetresPerSecond, double massKg, PhysicalCoefficients coefficients)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        if (!double.IsFinite(grade) || !double.IsFinite(speedMetresPerSecond) || !double.IsFinite(massKg) || massKg <= 0 || speedMetresPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(speedMetresPerSecond));
        }

        var incline = Math.Atan(grade);
        var gravityForce = massKg * Gravity * Math.Sin(incline);
        var rollingForce = massKg * Gravity * Math.Cos(incline) * coefficients.Crr;
        var aerodynamicForce = .5 * coefficients.AirDensity * coefficients.CdA * speedMetresPerSecond * speedMetresPerSecond;
        return Math.Max(0, (gravityForce + rollingForce + aerodynamicForce) * speedMetresPerSecond / coefficients.DrivetrainEfficiency);
    }
}
