using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Physics;

public interface IPhysicsCalibrator
{
    PhysicalCalibrationResult Calibrate(
        RiderProfile profile,
        IReadOnlyList<CleanedActivity> activities);
}
