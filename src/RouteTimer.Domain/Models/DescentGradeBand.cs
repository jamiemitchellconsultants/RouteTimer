namespace RouteTimer.Domain.Models;

public sealed record DescentGradeBand(string Key, double ConservativeCapMetresPerSecond)
{
    public static DescentGradeBand Mild { get; } = new("mild", 13);
    public static DescentGradeBand Medium { get; } = new("medium", 16);
    public static DescentGradeBand Steep { get; } = new("steep", 18);

    public static IReadOnlyList<DescentGradeBand> All { get; } =
        Array.AsReadOnly([Mild, Medium, Steep]);

    public static DescentGradeBand? Find(double gradient)
    {
        if (!double.IsFinite(gradient)) return null;
        if (gradient >= -.04 && gradient <= -.02) return Mild;
        if (gradient >= -.08 && gradient < -.04) return Medium;
        return gradient < -.08 ? Steep : null;
    }
}
