using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Models;

public sealed record ModelStatusResult(
    bool IsReady,
    string? BlockingReason,
    RiderModelSnapshot? CurrentModel,
    AnalysisJob? RebuildJob);
