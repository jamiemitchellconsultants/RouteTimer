using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components.Adjustments;
using RouteTimer.Client.Tests.Fakes;

namespace RouteTimer.Client.Tests;

public sealed class ZoneShiftEditorTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();
    private readonly Guid predictionId = Guid.NewGuid();

    public ZoneShiftEditorTests() => Services.AddSingleton<IRouteTimerApiClient>(api);

    private IRenderedComponent<ZoneShiftEditor> RenderEditor() =>
        Render<ZoneShiftEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));

    // Break caught: a gradient row with neither bound set is submittable, and the server always
    // rejects it with a 400.
    [Fact]
    public void Submit_is_disabled_while_a_gradient_row_has_no_bound()
    {
        var cut = RenderEditor();
        Assert.False(cut.Find("[data-testid=zone-shift-submit]").HasAttribute("disabled"));

        cut.Find("[data-testid=zone-shift-add-assignment]").Click();

        Assert.True(cut.Find("[data-testid=zone-shift-submit]").HasAttribute("disabled"));

        cut.Find("[data-testid=zone-shift-min-1]").Input("0.05");

        Assert.False(cut.Find("[data-testid=zone-shift-submit]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_max_only_gradient_row_is_accepted()
    {
        var cut = RenderEditor();
        cut.Find("[data-testid=zone-shift-add-assignment]").Click();
        cut.Find("[data-testid=zone-shift-max-1]").Input("-0.02");

        Assert.False(cut.Find("[data-testid=zone-shift-submit]").HasAttribute("disabled"));
    }

    [Fact]
    public void Switching_a_row_to_all_segments_clears_its_gradient_bounds()
    {
        var cut = RenderEditor();
        cut.Find("[data-testid=zone-shift-add-assignment]").Click();
        cut.Find("[data-testid=zone-shift-min-1]").Input("0.05");

        cut.Find("[data-testid=zone-shift-selector-1]").Change("all-segments");

        // Two all-segments rows are not allowed, so submit stays disabled - but the row itself no
        // longer carries the stale gradient bound.
        Assert.True(cut.Find("[data-testid=zone-shift-submit]").HasAttribute("disabled"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=zone-shift-min-1]"));
    }
}
