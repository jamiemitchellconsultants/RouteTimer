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

        var cell = model.Cells.Single(value =>
            value.GradeKey == grade.Key && value.CurvatureKey == curvature.Key);
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
