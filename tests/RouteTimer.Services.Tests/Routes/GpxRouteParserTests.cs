using RouteTimer.Services.Routes;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Routes;

public sealed class GpxRouteParserTests
{
    [Fact]
    public async Task Parse_accepts_gpx_without_timestamps()
    {
        await using var input = GpxFixtures.Route((51.0, -2.0, 50), (51.001, -2.0, 55));

        var route = await new GpxRouteParser().ParseAsync(input, CancellationToken.None);

        Assert.Equal(2, route.Points.Count);
        Assert.Equal(55, route.Points[1].ElevationMetres);
        Assert.Equal("Test route", route.Name);
    }

    [Fact]
    public async Task Parse_rejects_doctype()
    {
        await using var input = GpxFixtures.WithDoctype();

        await Assert.ThrowsAsync<RouteInputException>(() => new GpxRouteParser().ParseAsync(input, CancellationToken.None));
    }
}
