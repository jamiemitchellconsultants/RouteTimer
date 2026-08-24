using Bunit;
using RouteTimer.Client.Pages;

namespace RouteTimer.Client.Tests;

public sealed class ProfilePageTests : BunitContext
{
    [Fact]
    public void Profile_shows_rider_and_bike_weight_inputs()
    {
        var cut = Render<Profile>();

        Assert.Equal(2, cut.FindAll("input[type=number]").Count);
    }
}
