namespace RouteTimer.Domain.Models;

public sealed record PowerModel(IReadOnlyList<PowerBand> Bands, double GlobalTypicalWatts);
