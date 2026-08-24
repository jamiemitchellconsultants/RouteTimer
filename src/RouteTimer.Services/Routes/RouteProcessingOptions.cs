namespace RouteTimer.Services.Routes;

public sealed record RouteProcessingOptions(
    double SegmentMetres,
    double ElevationWindowMetres,
    double MinModelGrade,
    double MaxModelGrade)
{
    public static RouteProcessingOptions Default { get; } = new(25, 100, -.20, .20);
}
