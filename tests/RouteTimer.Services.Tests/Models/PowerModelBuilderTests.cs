using RouteTimer.Domain.Models;
using RouteTimer.Domain.Profile;
using RouteTimer.Services.Models;

namespace RouteTimer.Services.Tests.Models;

public sealed class PowerModelBuilderTests
{
    [Fact]
    public void Build_uses_robust_median_and_distinct_activity_coverage()
    {
        var activities = ModelFixtures.ThreeActivities([180, 200, 1000]);

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        var flatEarly = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "0:30");
        Assert.InRange(flatEarly.TypicalWatts, 180, 220);
        Assert.Equal(ConfidenceLevel.High, flatEarly.Confidence);
    }

    [Fact]
    public void Build_always_produces_the_full_40_cell_grid()
    {
        var activities = ModelFixtures.ThreeActivities([180, 200, 1000]);

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        Assert.Equal(40, model.Bands.Count);
        Assert.Equal(8, model.Bands.Select(band => band.GradeKey).Distinct().Count());
        Assert.Equal(5, model.Bands.Select(band => band.DurationKey).Distinct().Count());
        var combos = model.Bands.Select(band => (band.GradeKey, band.DurationKey)).Distinct().Count();
        Assert.Equal(40, combos);

        // A cell that received no direct evidence still gets a sensible shrunk value, not a gap.
        var untouched = model.Bands.Single(band => band.GradeKey == "9:100" && band.DurationKey == "180:+");
        Assert.Equal(0, untouched.ActivityCount);
        Assert.Equal(TimeSpan.Zero, untouched.Evidence);
        Assert.Equal(0, untouched.ShrinkageWeight);
        Assert.True(untouched.TypicalWatts > 0);
    }

    [Fact]
    public void Build_shrinks_empty_cells_through_gradient_then_duration_then_global_reference()
    {
        var activities = ModelFixtures.GradientDurationSpread();

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        // "9:100" has zero evidence anywhere, so its empty cells fall through to duration-only, then global.
        var durationOnlyFallback = model.Bands.Single(band => band.GradeKey == "9:100" && band.DurationKey == "60:120");
        Assert.Equal(300, durationOnlyFallback.TypicalWatts);
        Assert.Equal(0, durationOnlyFallback.ActivityCount);
        Assert.Equal(TimeSpan.Zero, durationOnlyFallback.Evidence);
        Assert.Equal(0, durationOnlyFallback.ShrinkageWeight);

        var globalFallback = model.Bands.Single(band => band.GradeKey == "9:100" && band.DurationKey == "120:180");
        Assert.Equal(250, globalFallback.TypicalWatts);
        Assert.Equal(250, model.GlobalTypicalWatts);

        // "-1:1" has evidence elsewhere (durations 0:30 and 60:120), so its empty "30:60" cell uses the
        // gradient-only reference rather than falling further to duration-only or global.
        var gradientOnlyFallback = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "30:60");
        Assert.Equal(200, gradientOnlyFallback.TypicalWatts);
    }

    [Fact]
    public void Build_keeps_a_strongly_evidenced_cell_close_to_its_own_median_despite_a_divergent_reference()
    {
        var activities = ModelFixtures.StrongEvidenceWithDivergentGradientReference();

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        var cell = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "0:30");
        Assert.Equal(3, cell.ActivityCount);
        Assert.True(cell.ShrinkageWeight > 0.5, $"expected shrinkage weight > 0.5, was {cell.ShrinkageWeight}");
        // Direct median is 200; the gradient-only reference (pulled by the 800W activities elsewhere in
        // the same gradient band) is 800. The blended value should land far closer to 200 than to 800.
        Assert.True(Math.Abs(cell.TypicalWatts - 200) < Math.Abs(cell.TypicalWatts - 800),
            $"expected {cell.TypicalWatts} to be closer to 200 than to 800");
    }

    [Fact]
    public void Build_caps_a_dominant_activitys_contribution_to_a_cell()
    {
        var activities = ModelFixtures.DominantActivityCell();

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        var cell = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "0:30");
        // Uncapped, the 600-minute activity's weight would swamp the other two and pull the median to
        // 1000W. The cap holds it down near the smaller activities' values instead.
        Assert.True(cell.TypicalWatts < 500, $"expected capped result well below 1000, was {cell.TypicalWatts}");
        Assert.True(cell.TypicalWatts > 100, $"expected capped result near the small activities, was {cell.TypicalWatts}");
    }

    [Fact]
    public void Build_marks_a_cell_high_confidence_at_exactly_the_15_minute_3_activity_threshold()
    {
        var activities = ModelFixtures.CellWithEvidence(activityCount: 3, minutesEach: 5);

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        var cell = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "0:30");
        Assert.Equal(TimeSpan.FromMinutes(15), cell.Evidence);
        Assert.Equal(3, cell.ActivityCount);
        Assert.Equal(ConfidenceLevel.High, cell.Confidence);
    }

    [Fact]
    public void Build_does_not_mark_high_confidence_just_under_the_evidence_threshold()
    {
        var activities = ModelFixtures.CellWithEvidence(activityCount: 3, minutesEach: 4.99);

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        var cell = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "0:30");
        Assert.NotEqual(ConfidenceLevel.High, cell.Confidence);
    }

    [Fact]
    public void Build_does_not_mark_high_confidence_just_under_the_activity_count_threshold()
    {
        var activities = ModelFixtures.CellWithEvidence(activityCount: 2, minutesEach: 10);

        var model = new PowerModelBuilder().Build(new RiderProfile(75, 10), activities);

        var cell = model.Bands.Single(band => band.GradeKey == "-1:1" && band.DurationKey == "0:30");
        Assert.Equal(TimeSpan.FromMinutes(20), cell.Evidence);
        Assert.Equal(2, cell.ActivityCount);
        Assert.NotEqual(ConfidenceLevel.High, cell.Confidence);
        Assert.Equal(ConfidenceLevel.Medium, cell.Confidence);
    }

    [Fact]
    public void Build_throws_when_no_eligible_power_evidence_is_available()
    {
        var activities = ModelFixtures.ThreeActivities([]);

        var exception = Assert.Throws<InvalidOperationException>(() => new PowerModelBuilder().Build(new RiderProfile(75, 10), activities));
        Assert.Equal("No eligible power evidence is available.", exception.Message);
    }
}
