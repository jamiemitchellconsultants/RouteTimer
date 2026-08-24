using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Models;

public interface IPowerModelBuilder { PowerModel Build(RiderProfile profile, IReadOnlyList<CleanedActivity> activities); }
