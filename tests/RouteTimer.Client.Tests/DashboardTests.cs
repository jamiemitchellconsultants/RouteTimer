using Bunit;
using RouteTimer.Client.Pages;

namespace RouteTimer.Client.Tests;

public sealed class DashboardTests : BunitContext
{
    [Fact]
    public void Dashboard_shows_route_timer_heading()
    {
        var cut = Render<Home>();

        cut.Find("h1").MarkupMatches("<h1>RouteTimer dashboard</h1>");
    }
}
