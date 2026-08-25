namespace RouteTimer.Domain.Models;

public sealed class DescentLimitModel
{
    private static readonly TimeSpan MinimumLearnedEvidence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HighConfidenceEvidence = TimeSpan.FromMinutes(20);
    private const int MinimumLearnedActivityCount = 2;
    private const int HighConfidenceActivityCount = 3;

    private static readonly IReadOnlyDictionary<string, int> GradeOrder =
        DescentGradeBand.All.Select((band, index) => (band.Key, index))
            .ToDictionary(value => value.Key, value => value.index, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, int> CurvatureOrder =
        DescentCurvatureBand.All.Select((band, index) => (band.Key, index))
            .ToDictionary(value => value.Key, value => value.index, StringComparer.Ordinal);

    public static DescentLimitModel Conservative { get; } = new(CreateConservativeCells());

    public DescentLimitModel(IReadOnlyList<DescentLimitCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count != GradeOrder.Count * CurvatureOrder.Count)
            throw new ArgumentException("A descent limit model must contain exactly nine cells.", nameof(cells));

        var copy = cells.ToArray();
        foreach (var cell in copy)
        {
            if (cell is null)
                throw new ArgumentException("Descent limit cells cannot be null.", nameof(cells));
            if (cell.GradeKey is null || cell.CurvatureKey is null ||
                !GradeOrder.ContainsKey(cell.GradeKey) || !CurvatureOrder.ContainsKey(cell.CurvatureKey))
                throw new ArgumentException("A descent limit cell has an unknown grade or curvature key.", nameof(cells));
            if (!double.IsFinite(cell.SpeedCapMetresPerSecond) || cell.SpeedCapMetresPerSecond <= 0 || cell.SpeedCapMetresPerSecond > 20)
                throw new ArgumentException("Descent speed caps must be finite and in the range (0, 20].", nameof(cells));
            if (cell.Evidence < TimeSpan.Zero)
                throw new ArgumentException("Descent evidence cannot be negative.", nameof(cells));
            if (cell.ActivityCount < 0)
                throw new ArgumentException("Descent activity count cannot be negative.", nameof(cells));
            if (!Enum.IsDefined(cell.Confidence))
                throw new ArgumentException("Descent confidence is invalid.", nameof(cells));
            if (!SatisfiesCellInvariants(cell))
                throw new ArgumentException("Descent cell provenance, evidence, confidence, or fallback cap is contradictory.", nameof(cells));
        }

        if (copy.Select(cell => (cell.GradeKey, cell.CurvatureKey)).Distinct().Count() != copy.Length)
            throw new ArgumentException("A descent limit model must contain each grade/curvature cell exactly once.", nameof(cells));

        var ordered = copy
            .OrderBy(cell => GradeOrder[cell.GradeKey])
            .ThenBy(cell => CurvatureOrder[cell.CurvatureKey])
            .ToArray();
        Cells = Array.AsReadOnly(ordered);
        WasLearned = ordered.Any(cell => !cell.IsFallback);
    }

    public IReadOnlyList<DescentLimitCell> Cells { get; }
    public bool WasLearned { get; }

    public static bool SatisfiesCellInvariants(DescentLimitCell? cell)
    {
        if (cell is null ||
            cell.GradeKey is null ||
            cell.CurvatureKey is null ||
            !GradeOrder.ContainsKey(cell.GradeKey) ||
            !CurvatureOrder.ContainsKey(cell.CurvatureKey) ||
            !double.IsFinite(cell.SpeedCapMetresPerSecond) ||
            cell.SpeedCapMetresPerSecond <= 0 ||
            cell.SpeedCapMetresPerSecond > 20 ||
            cell.Evidence < TimeSpan.Zero ||
            cell.ActivityCount < 0 ||
            !Enum.IsDefined(cell.Confidence))
        {
            return false;
        }

        if (cell.IsFallback)
        {
            return cell.Confidence == ConfidenceLevel.Low &&
                   (cell.Evidence < MinimumLearnedEvidence || cell.ActivityCount < MinimumLearnedActivityCount) &&
                   cell.SpeedCapMetresPerSecond <= ConservativeCap(cell.GradeKey, cell.CurvatureKey);
        }

        if (cell.Evidence < MinimumLearnedEvidence || cell.ActivityCount < MinimumLearnedActivityCount)
        {
            return false;
        }

        var expectedConfidence = cell.Evidence >= HighConfidenceEvidence && cell.ActivityCount >= HighConfidenceActivityCount
            ? ConfidenceLevel.High
            : ConfidenceLevel.Medium;
        return cell.Confidence == expectedConfidence;
    }

    private static IReadOnlyList<DescentLimitCell> CreateConservativeCells() =>
        DescentGradeBand.All.SelectMany(grade => DescentCurvatureBand.All.Select(curvature =>
        {
            var cap = ConservativeCap(grade.Key, curvature.Key);
            return new DescentLimitCell(
                grade.Key,
                curvature.Key,
                cap,
                TimeSpan.Zero,
                0,
                ConfidenceLevel.Low,
                true);
        })).ToArray();

    private static double ConservativeCap(string gradeKey, string curvatureKey)
    {
        var grade = DescentGradeBand.All.Single(value => value.Key == gradeKey);
        var curvature = DescentCurvatureBand.All.Single(value => value.Key == curvatureKey);
        var curvatureCap = curvature.LowerBoundaryPerMetre > 0
            ? Math.Sqrt(2 / curvature.LowerBoundaryPerMetre)
            : 20;
        return Math.Min(grade.ConservativeCapMetresPerSecond, Math.Min(20, curvatureCap));
    }
}
