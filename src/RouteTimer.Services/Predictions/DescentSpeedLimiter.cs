using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Predictions;

public sealed class DescentSpeedLimiter : IDescentSpeedLimiter
{
    public DescentLimitEstimate Resolve(
        double gradient,
        double curvaturePerMetre,
        DescentLimitModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var grade = DescentGradeBand.Find(gradient);
        if (grade is null)
            return new DescentLimitEstimate(double.PositiveInfinity, ConfidenceLevel.High, false);

        var curvature = DescentCurvatureBand.Find(curvaturePerMetre);
        if (curvature is null)
        {
            var conservativeCap = ConservativeCap(grade, curvaturePerMetre);
            return new DescentLimitEstimate(conservativeCap, ConfidenceLevel.Low, true);
        }

        var matching = model.Cells?
            .Where(value => value is not null && value.GradeKey == grade.Key && value.CurvatureKey == curvature.Key)
            .Take(2)
            .ToArray();
        if (matching is null || matching.Length != 1 || !DescentLimitModel.SatisfiesCellInvariants(matching[0]))
        {
            var conservativeCap = ConservativeCap(grade, curvaturePerMetre);
            if (matching is { Length: 1 } &&
                double.IsFinite(matching[0].SpeedCapMetresPerSecond) &&
                matching[0].SpeedCapMetresPerSecond > 0)
            {
                conservativeCap = Math.Min(conservativeCap, matching[0].SpeedCapMetresPerSecond);
            }

            return new DescentLimitEstimate(conservativeCap, ConfidenceLevel.Low, true);
        }

        var cell = matching[0];
        var actualCurvatureCap = CurvatureCap(curvaturePerMetre);
        var cap = Math.Min(cell.SpeedCapMetresPerSecond, Math.Min(20, actualCurvatureCap));
        if (cell.IsFallback)
            cap = Math.Min(cap, grade.ConservativeCapMetresPerSecond);

        return new DescentLimitEstimate(cap, cell.Confidence, cell.IsFallback);
    }

    private static double ConservativeCap(DescentGradeBand grade, double curvaturePerMetre) =>
        Math.Min(grade.ConservativeCapMetresPerSecond, Math.Min(20, CurvatureCap(curvaturePerMetre)));

    private static double CurvatureCap(double curvaturePerMetre) =>
        curvaturePerMetre > 0 ? Math.Sqrt(2 / curvaturePerMetre) : 20;
}
