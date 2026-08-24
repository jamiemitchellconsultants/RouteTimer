using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Tests.Models;

public sealed class PowerModelBandsTests
{
    [Theory]
    [InlineData(-0.5, "-100:-6")]
    [InlineData(-0.06, "-6:-3")] // half-open [lower, upper): exactly -6% belongs to the "-6:-3" band, not "-100:-6"
    [InlineData(-0.045, "-6:-3")]
    [InlineData(-0.03, "-3:-1")]
    [InlineData(-0.02, "-3:-1")]
    [InlineData(-0.01, "-1:1")]
    [InlineData(0, "-1:1")]
    [InlineData(0.01, "1:3")]
    [InlineData(0.03, "3:6")]
    [InlineData(0.06, "6:9")]
    [InlineData(0.09, "9:100")]
    [InlineData(0.5, "9:100")]
    public void FindGradientBand_resolves_boundaries_and_extremes(double gradient, string expectedKey)
    {
        var band = PowerModelBands.FindGradientBand(gradient);

        Assert.Equal(expectedKey, band.Key);
    }

    [Fact]
    public void FindGradientBand_never_throws_on_non_finite_input()
    {
        Assert.NotNull(PowerModelBands.FindGradientBand(double.NaN));
        Assert.NotNull(PowerModelBands.FindGradientBand(double.PositiveInfinity));
        Assert.NotNull(PowerModelBands.FindGradientBand(double.NegativeInfinity));
    }

    [Theory]
    [InlineData(0, "0:30")]
    [InlineData(29, "0:30")]
    [InlineData(30, "30:60")] // half-open [lower, upper): exactly 30 minutes belongs to the "30:60" band
    [InlineData(59, "30:60")]
    [InlineData(60, "60:120")]
    [InlineData(119, "60:120")]
    [InlineData(120, "120:180")]
    [InlineData(179, "120:180")]
    [InlineData(180, "180:+")]
    [InlineData(600, "180:+")]
    public void FindDurationBand_resolves_boundaries_and_extremes(int minutes, string expectedKey)
    {
        var band = PowerModelBands.FindDurationBand(TimeSpan.FromMinutes(minutes));

        Assert.Equal(expectedKey, band.Key);
    }

    [Fact]
    public void Bands_cover_eight_gradients_and_five_durations()
    {
        Assert.Equal(8, PowerModelBands.Gradient.Count);
        Assert.Equal(5, PowerModelBands.Duration.Count);
    }
}
