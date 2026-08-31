using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components.Adjustments;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Adjustments;

namespace RouteTimer.Client.Tests;

public sealed class AdjustmentBuilderTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();
    private readonly Guid predictionId = Guid.NewGuid();

    public AdjustmentBuilderTests() => Services.AddSingleton<IRouteTimerApiClient>(api);

    private static PacingStrategyCapabilityResponse Capabilities(
        bool segmentGains = false, bool npIf = false, bool timeTarget = false,
        bool zoneShift = false, bool matchBurning = false) =>
        new(true, segmentGains, npIf, timeTarget, zoneShift, matchBurning, 65536, 10, 10);

    private IRenderedComponent<AdjustmentBuilder> RenderBuilder(PacingStrategyCapabilityResponse capabilities) =>
        Render<AdjustmentBuilder>(parameters => parameters
            .Add(builder => builder.PredictionId, predictionId)
            .Add(builder => builder.Capabilities, capabilities));

    // Break caught: a strategy is listed as available but its editor is never rendered, so the
    // feature cannot be reached from the UI at all.
    [Theory]
    [InlineData("segment-gains", "segment-gains-editor")]
    [InlineData("np-if", "np-if-editor")]
    [InlineData("time-target", "time-target-editor")]
    [InlineData("zone-shift", "zone-shift-editor")]
    [InlineData("match-burning", "match-burning-editor")]
    public void Each_enabled_strategy_renders_its_editor(string strategy, string editorTestId)
    {
        var capabilities = strategy switch
        {
            "segment-gains" => Capabilities(segmentGains: true),
            "np-if" => Capabilities(npIf: true),
            "time-target" => Capabilities(timeTarget: true),
            "zone-shift" => Capabilities(zoneShift: true),
            "match-burning" => Capabilities(matchBurning: true),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
        };

        var cut = RenderBuilder(capabilities);

        Assert.NotNull(cut.Find($"[data-testid={editorTestId}]"));
    }

    [Fact]
    public void A_disabled_strategy_does_not_render_its_editor()
    {
        var cut = RenderBuilder(Capabilities(timeTarget: true));

        Assert.NotNull(cut.Find("[data-testid=time-target-editor]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=np-if-editor]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=zone-shift-editor]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=match-burning-editor]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=segment-gains-editor]"));
    }

    [Fact]
    public void Every_enabled_strategy_renders_alongside_the_others()
    {
        var cut = RenderBuilder(Capabilities(true, true, true, true, true));

        Assert.NotNull(cut.Find("[data-testid=segment-gains-editor]"));
        Assert.NotNull(cut.Find("[data-testid=np-if-editor]"));
        Assert.NotNull(cut.Find("[data-testid=time-target-editor]"));
        Assert.NotNull(cut.Find("[data-testid=zone-shift-editor]"));
        Assert.NotNull(cut.Find("[data-testid=match-burning-editor]"));
    }
}
