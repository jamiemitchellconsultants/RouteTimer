namespace RouteTimer.Domain.Models;

public sealed record DescentCurvatureBand(string Key, double LowerBoundaryPerMetre)
{
    public static DescentCurvatureBand Straight { get; } = new("straight", 0);
    public static DescentCurvatureBand Moderate { get; } = new("moderate", .002);
    public static DescentCurvatureBand Tight { get; } = new("tight", .01);

    public static IReadOnlyList<DescentCurvatureBand> All { get; } =
        Array.AsReadOnly([Straight, Moderate, Tight]);

    public static DescentCurvatureBand? Find(double curvaturePerMetre)
    {
        if (!double.IsFinite(curvaturePerMetre) || curvaturePerMetre < 0) return null;
        if (curvaturePerMetre < .002) return Straight;
        if (curvaturePerMetre < .01) return Moderate;
        return Tight;
    }
}
