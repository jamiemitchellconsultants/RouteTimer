using RouteTimer.Domain.Models;
using RouteTimer.Services.Models;

namespace RouteTimer.Services.Tests.Models;

public sealed class PowerLookupTests
{
    [Fact]
    public void GetWatts_interpolates_between_adjacent_gradient_bands()
    {
        var model = ModelFixtures.SimpleModel();

        var estimate = new PowerLookup(model).GetWatts(0.015, TimeSpan.FromMinutes(10));

        Assert.InRange(estimate.Watts, 199, 241);
        Assert.False(estimate.Extrapolated);
    }

    [Fact]
    public void GetWatts_bilinearly_interpolates_across_gradient_and_duration()
    {
        var model = ModelFixtures.GridModel();

        // Gradient .015 brackets "-1:1" (anchor 0) / "1:3" (anchor .02).
        // Elapsed 40min brackets "0:30" (anchor 15min) / "30:60" (anchor 45min).
        // At gradeLower: 180 -> 200 blended 0.8333 of the way = 196.66667
        // At gradeUpper: 260 -> 300 blended 0.8333 of the way = 293.33333
        // Across gradient at 0.75 of the way: 196.66667 -> 293.33333 = 269.16667
        var estimate = new PowerLookup(model).GetWatts(0.015, TimeSpan.FromMinutes(40));

        Assert.InRange(estimate.Watts, 269.1, 269.3);
        Assert.False(estimate.Extrapolated);
        Assert.Equal("interpolated", estimate.Reason);
        // Weakest of the four corners: (1:3, 30:60) is Medium, the rest High.
        Assert.Equal(ConfidenceLevel.Medium, estimate.Confidence);
    }

    [Fact]
    public void GetWatts_extrapolates_beyond_the_open_ended_high_duration_band()
    {
        var model = ModelFixtures.GridModel();

        // Gradient .02 sits exactly on the "1:3" anchor (no gradient interpolation needed).
        // Elapsed 4 hours is beyond the "180:+" band's anchor, and "180:+" is open-ended high,
        // so this collapses to the "1:3"/"180:+" band's own value with duration extrapolation.
        var estimate = new PowerLookup(model).GetWatts(0.02, TimeSpan.FromHours(4));

        Assert.Equal(340, estimate.Watts, 3);
        Assert.True(estimate.Extrapolated);
        Assert.Equal("nearest-band", estimate.Reason);
        Assert.Equal(ConfidenceLevel.High, estimate.Confidence);
    }

    [Fact]
    public void GetWatts_does_not_extrapolate_for_low_elapsed_within_the_first_duration_band()
    {
        var model = ModelFixtures.SimpleModel();

        // 2 minutes is far below the "0:30" band's 15-minute anchor, but "0:30" has LowerBound
        // TimeSpan.Zero (not open-ended), so this must NOT be flagged as extrapolation on the
        // duration axis. The naive "beyond the smallest anchor" rule would incorrectly flag this.
        var estimate = new PowerLookup(model).GetWatts(0.015, TimeSpan.FromMinutes(2));

        Assert.False(estimate.Extrapolated);
        Assert.Equal("interpolated", estimate.Reason);
    }

    [Fact]
    public void GetWatts_falls_back_to_same_gradient_nearest_duration_for_a_missing_corner()
    {
        var model = ModelFixtures.SparseCornerModel();

        // Needs the ("-1:1", "30:60") corner, which has no direct evidence. The nearest same-gradient
        // duration data is ("-1:1", "0:30") (anchor 15min, 30min away from "30:60"'s 45min anchor),
        // which is closer than ("-1:1", "60:120") (anchor 90min, 45min away).
        var estimate = new PowerLookup(model).GetWatts(0.015, TimeSpan.FromMinutes(40));

        // Neither axis is out of range, but the fallback-substituted corner still marks this as
        // not-genuine grid interpolation.
        Assert.True(estimate.Extrapolated);
        Assert.Equal("nearest-band", estimate.Reason);
        Assert.Equal(ConfidenceLevel.High, estimate.Confidence);
        // Sanity: the result should still be a plausible blend, not wildly outside the corner range.
        Assert.InRange(estimate.Watts, 180, 300);
    }

    [Fact]
    public void GetWatts_falls_back_to_global_typical_watts_when_a_corner_has_no_matching_evidence_at_all()
    {
        var model = ModelFixtures.SimpleModel();

        // SimpleModel only has "-1:1" and "1:3" gradient bands, both at "0:30" duration. A query in the
        // "6:9" gradient band at 200 minutes elapsed needs corners that share neither gradient key
        // ("3:6"/"6:9") nor duration key ("180:+") with anything in the model, so both required corners
        // must fall all the way back to GlobalTypicalWatts/Low.
        var estimate = new PowerLookup(model).GetWatts(0.07, TimeSpan.FromMinutes(200));

        Assert.Equal(220, estimate.Watts, 3);
        Assert.Equal(ConfidenceLevel.Low, estimate.Confidence);
        Assert.True(estimate.Extrapolated);
        Assert.Equal("nearest-band", estimate.Reason);
    }

    [Fact]
    public void GetWatts_returns_global_typical_watts_for_an_empty_model()
    {
        var model = new PowerModel([], 215);

        var estimate = new PowerLookup(model).GetWatts(0.02, TimeSpan.FromMinutes(30));

        Assert.Equal(215, estimate.Watts);
        Assert.Equal(ConfidenceLevel.Low, estimate.Confidence);
        Assert.True(estimate.Extrapolated);
        Assert.Equal("no-band-evidence", estimate.Reason);
    }
}
