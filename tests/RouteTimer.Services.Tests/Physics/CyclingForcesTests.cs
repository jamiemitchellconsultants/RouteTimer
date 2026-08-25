using RouteTimer.Domain.Physics;
using RouteTimer.Services.Physics;

namespace RouteTimer.Services.Tests.Physics;

public sealed class CyclingForcesTests
{
    [Theory]
    [InlineData(0.00, 10.0, 75, 10, 245, 8)]
    [InlineData(0.05, 5.0, 75, 10, 261, 12)]
    public void Required_power_matches_known_tolerance(double grade, double speed, double riderKg, double bikeKg, double expectedWatts, double tolerance)
    {
        var watts = CyclingForces.RequiredRiderPower(grade, speed, riderKg + bikeKg, PhysicalCoefficients.Default);

        Assert.InRange(watts, expectedWatts - tolerance, expectedWatts + tolerance);
    }

    // Break caught: gravity loses its direction and is treated as positive resistance on descents.
    [Fact]
    public void Gravity_force_is_positive_uphill_and_negative_downhill()
    {
        Assert.True(CyclingForces.GravityForce(.05, 85) > 0);
        Assert.True(CyclingForces.GravityForce(-.05, 85) < 0);
        Assert.Equal(0, CyclingForces.GravityForce(0, 85));
    }

    // Break caught: rolling resistance omits slope projection or aerodynamic drag loses its squared-speed term.
    [Fact]
    public void Rolling_and_aerodynamic_resistance_are_positive()
    {
        var rolling = CyclingForces.RollingForce(.10, 85, .005);
        var expectedRolling = 85 * 9.80665 * Math.Cos(Math.Atan(.10)) * .005;
        var aerodynamic = CyclingForces.AerodynamicForce(10, 1.225, .32);

        Assert.Equal(expectedRolling, rolling, 12);
        Assert.Equal(19.6, aerodynamic, 12);
        Assert.True(rolling > 0);
        Assert.True(aerodynamic > 0);
    }

    // Break caught: required rider power ignores the inertial force needed to change kinetic energy.
    [Fact]
    public void Required_power_includes_inertial_force_balance()
    {
        var watts = CyclingForces.RequiredRiderPower(0, 10, 85, PhysicalCoefficients.Default, .2);

        Assert.Equal(420.286868556701, watts, 10);
    }

    // Break caught: reusable force functions allow non-finite inputs to contaminate calibration or prediction.
    [Fact]
    public void Reusable_force_calculations_reject_non_finite_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.GravityForce(double.NaN, 85));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.GravityForce(0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.GravityForce(1, double.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.RollingForce(0, 85, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.RollingForce(0, double.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.AerodynamicForce(double.PositiveInfinity, 1.225, .32));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.AerodynamicForce(10, double.NaN, .32));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.AerodynamicForce(10, 1.225, double.NegativeInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => CyclingForces.AerodynamicForce(double.MaxValue, 1.225, .32));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CyclingForces.RequiredRiderPower(0, 10, 85, PhysicalCoefficients.Default, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CyclingForces.RequiredRiderPower(0, 10, 85, new PhysicalCoefficients(.97, 1.225, .005, double.NaN)));
    }
}
