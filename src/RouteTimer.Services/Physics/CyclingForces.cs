using RouteTimer.Domain.Physics;

namespace RouteTimer.Services.Physics;

public static class CyclingForces
{
    public const double GravityMetresPerSecondSquared = 9.80665;

    public static double GravityForce(double grade, double massKg)
    {
        ValidateGradeAndMass(grade, massKg);
        var force = massKg * GravityMetresPerSecondSquared * Math.Sin(Math.Atan(grade));
        if (!double.IsFinite(force)) throw new ArgumentOutOfRangeException(nameof(massKg));
        return force;
    }

    public static double RollingForce(double grade, double massKg, double crr)
    {
        ValidateGradeAndMass(grade, massKg);
        if (!double.IsFinite(crr) || crr < 0) throw new ArgumentOutOfRangeException(nameof(crr));
        var force = massKg * GravityMetresPerSecondSquared * Math.Cos(Math.Atan(grade)) * crr;
        if (!double.IsFinite(force)) throw new ArgumentOutOfRangeException(nameof(massKg));
        return force;
    }

    public static double AerodynamicForce(double speedMetresPerSecond, double airDensity, double cdA)
    {
        if (!double.IsFinite(speedMetresPerSecond) || speedMetresPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(speedMetresPerSecond));
        if (!double.IsFinite(airDensity) || airDensity < 0)
            throw new ArgumentOutOfRangeException(nameof(airDensity));
        if (!double.IsFinite(cdA) || cdA < 0) throw new ArgumentOutOfRangeException(nameof(cdA));

        var force = .5 * airDensity * cdA * speedMetresPerSecond * speedMetresPerSecond;
        if (!double.IsFinite(force)) throw new ArgumentOutOfRangeException(nameof(speedMetresPerSecond));
        return force;
    }

    public static double RequiredRiderPower(
        double grade,
        double speedMetresPerSecond,
        double massKg,
        PhysicalCoefficients coefficients,
        double accelerationMetresPerSecondSquared = 0)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        ValidateGradeAndMass(grade, massKg);
        if (!double.IsFinite(speedMetresPerSecond) || speedMetresPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(speedMetresPerSecond));
        if (!double.IsFinite(accelerationMetresPerSecondSquared))
            throw new ArgumentOutOfRangeException(nameof(accelerationMetresPerSecondSquared));
        if (!double.IsFinite(coefficients.DrivetrainEfficiency) || coefficients.DrivetrainEfficiency <= 0)
            throw new ArgumentOutOfRangeException(nameof(coefficients));

        var totalForce = GravityForce(grade, massKg)
            + RollingForce(grade, massKg, coefficients.Crr)
            + AerodynamicForce(speedMetresPerSecond, coefficients.AirDensity, coefficients.CdA)
            + massKg * accelerationMetresPerSecondSquared;
        var riderPower = totalForce * speedMetresPerSecond / coefficients.DrivetrainEfficiency;
        if (!double.IsFinite(riderPower)) throw new ArgumentOutOfRangeException(nameof(speedMetresPerSecond));
        return Math.Max(0, riderPower);
    }

    private static void ValidateGradeAndMass(double grade, double massKg)
    {
        if (!double.IsFinite(grade)) throw new ArgumentOutOfRangeException(nameof(grade));
        if (!double.IsFinite(massKg) || massKg <= 0) throw new ArgumentOutOfRangeException(nameof(massKg));
    }
}
