namespace RouteTimer.Domain.Models;

public sealed class DescentLimitModel
{
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
            if (!GradeOrder.ContainsKey(cell.GradeKey) || !CurvatureOrder.ContainsKey(cell.CurvatureKey))
                throw new ArgumentException("A descent limit cell has an unknown grade or curvature key.", nameof(cells));
            if (!double.IsFinite(cell.SpeedCapMetresPerSecond) || cell.SpeedCapMetresPerSecond <= 0 || cell.SpeedCapMetresPerSecond > 20)
                throw new ArgumentException("Descent speed caps must be finite and in the range (0, 20].", nameof(cells));
            if (cell.Evidence < TimeSpan.Zero)
                throw new ArgumentException("Descent evidence cannot be negative.", nameof(cells));
            if (cell.ActivityCount < 0)
                throw new ArgumentException("Descent activity count cannot be negative.", nameof(cells));
            if (!Enum.IsDefined(cell.Confidence))
                throw new ArgumentException("Descent confidence is invalid.", nameof(cells));
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

    private static IReadOnlyList<DescentLimitCell> CreateConservativeCells() =>
        DescentGradeBand.All.SelectMany(grade => DescentCurvatureBand.All.Select(curvature =>
        {
            var curvatureCap = curvature.LowerBoundaryPerMetre > 0
                ? Math.Sqrt(2 / curvature.LowerBoundaryPerMetre)
                : 20;
            var cap = Math.Min(grade.ConservativeCapMetresPerSecond, Math.Min(20, curvatureCap));
            return new DescentLimitCell(
                grade.Key,
                curvature.Key,
                cap,
                TimeSpan.Zero,
                0,
                ConfidenceLevel.Low,
                true);
        })).ToArray();
}
