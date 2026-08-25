using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Models;
using RouteTimer.Services.Tests.Activities;

namespace RouteTimer.Services.Tests.Models;

public sealed class DescentLimitBuilderTests
{
    [Fact]
    public void Build_computes_the_linearly_interpolated_ninetieth_percentile()
    {
        var activities = new[]
        {
            Activity("slow", 400, 10, -.10, 0),
            Activity("typical", 400, 15, -.10, 0),
            Activity("fast", 400, 20, -.10, 0)
        };

        var cell = Cell(new DescentLimitBuilder().Build(activities), "steep", "straight");

        Assert.Equal(19, cell.SpeedCapMetresPerSecond, 12);
        Assert.Equal(TimeSpan.FromMinutes(20), cell.Evidence);
        Assert.Equal(3, cell.ActivityCount);
        Assert.Equal(ConfidenceLevel.High, cell.Confidence);
        Assert.False(cell.IsFallback);
    }

    [Fact]
    public void Build_shrinks_exactly_minimum_coverage_toward_the_grade_target()
    {
        var activities = new[]
        {
            Activity("one", 150, 20, -.10, 0),
            Activity("two", 150, 20, -.10, 0)
        };

        var cell = Cell(new DescentLimitBuilder().Build(activities), "steep", "straight");

        Assert.Equal(18.5, cell.SpeedCapMetresPerSecond, 12);
        Assert.Equal(TimeSpan.FromMinutes(5), cell.Evidence);
        Assert.Equal(2, cell.ActivityCount);
        Assert.Equal(ConfidenceLevel.Medium, cell.Confidence);
        Assert.False(cell.IsFallback);
    }

    [Fact]
    public void Build_uses_fallback_when_duration_is_just_below_five_minutes()
    {
        var activities = new[]
        {
            Activity("one", 149.5, 20, -.10, 0),
            Activity("two", 149.5, 20, -.10, 0)
        };

        var cell = Cell(new DescentLimitBuilder().Build(activities), "steep", "straight");

        Assert.Equal(18, cell.SpeedCapMetresPerSecond, 12);
        Assert.Equal(TimeSpan.FromSeconds(299), cell.Evidence);
        Assert.Equal(2, cell.ActivityCount);
        Assert.Equal(ConfidenceLevel.Low, cell.Confidence);
        Assert.True(cell.IsFallback);
    }

    [Fact]
    public void Build_uses_fallback_when_only_one_activity_supplies_long_duration()
    {
        var cell = Cell(
            new DescentLimitBuilder().Build([Activity("one", 1_200, 20, -.10, 0)]),
            "steep",
            "straight");

        Assert.Equal(18, cell.SpeedCapMetresPerSecond, 12);
        Assert.Equal(TimeSpan.FromMinutes(20), cell.Evidence);
        Assert.Equal(1, cell.ActivityCount);
        Assert.Equal(ConfidenceLevel.Low, cell.Confidence);
        Assert.True(cell.IsFallback);
    }

    [Fact]
    public void Build_allows_well_evidenced_cap_to_exceed_grade_shrinkage_target()
    {
        var activities = new[]
        {
            Activity("one", 400, 18, -.03, 0),
            Activity("two", 400, 18, -.03, 0),
            Activity("three", 400, 18, -.03, 0)
        };

        var cell = Cell(new DescentLimitBuilder().Build(activities), "mild", "straight");

        Assert.Equal(18, cell.SpeedCapMetresPerSecond, 12);
        Assert.Equal(ConfidenceLevel.High, cell.Confidence);
        Assert.False(cell.IsFallback);
    }

    [Fact]
    public void Build_lets_extreme_representative_curvature_override_the_two_metre_floor()
    {
        var activities = new[]
        {
            Activity("one", 400, 10, -.10, .8),
            Activity("two", 400, 10, -.10, .8),
            Activity("three", 400, 10, -.10, .8)
        };

        var cell = Cell(new DescentLimitBuilder().Build(activities), "steep", "tight");

        Assert.True(double.IsFinite(cell.SpeedCapMetresPerSecond));
        Assert.InRange(cell.SpeedCapMetresPerSecond, double.Epsilon, 1.5811388300841898);
        Assert.Equal(ConfidenceLevel.High, cell.Confidence);
        Assert.False(cell.IsFallback);
    }

    [Fact]
    public void Build_produces_all_nine_cells_with_learned_and_fallback_provenance()
    {
        var activities = new[]
        {
            Activity("one", 150, 15, -.06, .005),
            Activity("two", 150, 15, -.06, .005)
        };

        var model = new DescentLimitBuilder().Build(activities);

        Assert.Equal(9, model.Cells.Count);
        Assert.Equal(9, model.Cells.Select(cell => (cell.GradeKey, cell.CurvatureKey)).Distinct().Count());
        Assert.True(model.WasLearned);
        Assert.False(Cell(model, "medium", "moderate").IsFallback);
        Assert.True(Cell(model, "medium", "tight").IsFallback);
    }

    [Fact]
    public void Build_counts_only_adjacent_intervals_within_a_section_from_eligible_activities()
    {
        var eligible = Activity(
            "eligible",
            ActivityEligibility.Eligible,
            new Interval(150, 20, -.10, 0, false),
            new Interval(150, 20, -.10, 0, true),
            new Interval(150, 20, -.10, 0, false));
        var ineligible = Activity(
            "ineligible",
            ActivityEligibility.Ineligible,
            new Interval(600, 20, -.10, 0, false));

        var cell = Cell(new DescentLimitBuilder().Build([eligible, ineligible]), "steep", "straight");

        Assert.Equal(TimeSpan.FromMinutes(5), cell.Evidence);
        Assert.Equal(1, cell.ActivityCount);
        Assert.True(cell.IsFallback);
    }

    [Fact]
    public void Build_ignores_intervals_whose_ending_sample_is_outside_the_descent_grid()
    {
        var activity = Activity(
            "mixed",
            ActivityEligibility.Eligible,
            new Interval(300, 20, -.01, 0, false),
            new Interval(300, 20, -.06, -.001, false));

        var model = new DescentLimitBuilder().Build([activity]);

        Assert.All(model.Cells, cell =>
        {
            Assert.Equal(TimeSpan.Zero, cell.Evidence);
            Assert.Equal(0, cell.ActivityCount);
            Assert.True(cell.IsFallback);
        });
        Assert.False(model.WasLearned);
    }

    [Fact]
    public void Build_excludes_speed_below_two_metres_per_second_from_descent_evidence()
    {
        var activity = Activity("too slow", 600, 1.9999999999, -.06, .005);

        var cell = Cell(new DescentLimitBuilder().Build([activity]), "medium", "moderate");

        Assert.Equal(TimeSpan.Zero, cell.Evidence);
        Assert.Equal(0, cell.ActivityCount);
        Assert.True(cell.IsFallback);
    }

    [Fact]
    public void Build_is_independent_of_activity_order()
    {
        var activities = new[]
        {
            Activity("one", 200, 12, -.06, .005),
            Activity("two", 200, 16, -.06, .005),
            Activity("three", 800, 19, -.06, .005)
        };

        var forward = new DescentLimitBuilder().Build(activities);
        var reverse = new DescentLimitBuilder().Build(activities.Reverse().ToArray());

        Assert.Equal(forward.Cells, reverse.Cells);
    }

    [Fact]
    public void Model_takes_an_immutable_deterministically_ordered_copy()
    {
        var source = DescentLimitModel.Conservative.Cells.Reverse().ToArray();
        var originalFirst = source[0];

        var model = new DescentLimitModel(source);
        source[0] = source[0] with { SpeedCapMetresPerSecond = 2 };

        Assert.Equal(
            [
                ("mild", "straight"), ("mild", "moderate"), ("mild", "tight"),
                ("medium", "straight"), ("medium", "moderate"), ("medium", "tight"),
                ("steep", "straight"), ("steep", "moderate"), ("steep", "tight")
            ],
            model.Cells.Select(cell => (cell.GradeKey, cell.CurvatureKey)).ToArray());
        Assert.Contains(originalFirst, model.Cells);
        Assert.Throws<NotSupportedException>(() => ((IList<DescentLimitCell>)model.Cells)[0] = originalFirst);
    }

    [Fact]
    public void Model_rejects_missing_duplicate_or_unknown_grid_cells()
    {
        var valid = DescentLimitModel.Conservative.Cells.ToArray();

        Assert.Throws<ArgumentException>(() => new DescentLimitModel(valid[..^1]));

        var duplicate = valid.ToArray();
        duplicate[^1] = duplicate[0];
        Assert.Throws<ArgumentException>(() => new DescentLimitModel(duplicate));

        var unknown = valid.ToArray();
        unknown[0] = unknown[0] with { GradeKey = "unknown" };
        Assert.Throws<ArgumentException>(() => new DescentLimitModel(unknown));
    }

    [Theory]
    [InlineData(double.NaN, 0, 0)]
    [InlineData(double.PositiveInfinity, 0, 0)]
    [InlineData(10, -1, 0)]
    [InlineData(10, 0, -1)]
    [InlineData(10, 0, 0, (ConfidenceLevel)99)]
    public void Model_rejects_invalid_cell_values(
        double speedCap,
        double evidenceSeconds,
        int activityCount,
        ConfidenceLevel confidence = ConfidenceLevel.Low)
    {
        var cells = DescentLimitModel.Conservative.Cells.ToArray();
        cells[0] = cells[0] with
        {
            SpeedCapMetresPerSecond = speedCap,
            Evidence = TimeSpan.FromSeconds(evidenceSeconds),
            ActivityCount = activityCount,
            Confidence = confidence
        };

        Assert.Throws<ArgumentException>(() => new DescentLimitModel(cells));
    }

    private static DescentLimitCell Cell(DescentLimitModel model, string grade, string curvature) =>
        model.Cells.Single(cell => cell.GradeKey == grade && cell.CurvatureKey == curvature);

    private static CleanedActivity Activity(
        string name,
        double durationSeconds,
        double speed,
        double gradient,
        double curvature) =>
        Activity(name, ActivityEligibility.Eligible, new Interval(durationSeconds, speed, gradient, curvature, false));

    private static CleanedActivity Activity(
        string name,
        ActivityEligibility eligibility,
        params Interval[] intervals)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = new List<CleanRideSample>
        {
            Sample(start, 0, 0, 0, 0, false)
        };
        var elapsed = 0d;
        foreach (var interval in intervals)
        {
            elapsed += interval.DurationSeconds;
            samples.Add(Sample(
                start,
                elapsed,
                interval.Speed,
                interval.Gradient,
                interval.Curvature,
                interval.CrossesDiscontinuity));
        }

        var quality = new ActivityQuality(eligibility, 1, 1, 1, 1, new Dictionary<string, int>(), []);
        return new CleanedActivity(name, samples, TimeSpan.FromSeconds(elapsed), quality, ActivityFixtures.Metadata($"{name}.fit", start, start.AddSeconds(elapsed), null, null, null, null));
    }

    private static CleanRideSample Sample(
        DateTimeOffset start,
        double seconds,
        double speed,
        double gradient,
        double curvature,
        bool crossesDiscontinuity) =>
        new(
            start.AddSeconds(seconds),
            TimeSpan.FromSeconds(seconds),
            new GeoPoint(51, -2, 100),
            speed,
            0,
            140,
            85,
            crossesDiscontinuity,
            gradient,
            curvature);

    private readonly record struct Interval(
        double DurationSeconds,
        double Speed,
        double Gradient,
        double Curvature,
        bool CrossesDiscontinuity);
}
