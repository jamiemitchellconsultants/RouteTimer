using System.Globalization;
using System.Text.Json;
using Bunit;
using Bunit.JSInterop;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Pages;
using RouteTimer.Client.RouteBuilder;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Contracts.Predictions;

namespace RouteTimer.Client.Tests;

public sealed class PredictionAdjustmentShellTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();

    public PredictionAdjustmentShellTests()
    {
        Services.AddSingleton<IRouteTimerApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MapTiles:Url"] = "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
                ["MapTiles:Attribution"] = "&copy; OpenStreetMap contributors"
            })
            .Build());
        Services.AddSingleton(new BrowserInterop(JSInterop.JSRuntime));
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        var qr = JSInterop.SetupModule("./js/pace-tracker-qr.mjs");
        qr.SetupVoid("render", _ => true).SetVoidResult();
        qr.SetupVoid("clear", _ => true).SetVoidResult();

        var module = JSInterop.SetupModule("./js/route-visualization.js");
        module.SetupVoid("initializeMap", _ => true).SetVoidResult();
        module.SetupVoid("initializeProfiles", _ => true).SetVoidResult();
        module.SetupVoid("initializeComparisonProfiles", _ => true).SetVoidResult();
        module.SetupVoid("selectMapSequence", _ => true).SetVoidResult();
        module.SetupVoid("selectProfileSequence", _ => true).SetVoidResult();
        module.SetupVoid("disposeMap", _ => true).SetVoidResult();
        module.SetupVoid("disposeProfiles", _ => true).SetVoidResult();

        api.OnGetPacingStrategiesAsync = _ => Task.FromResult(DisabledCapabilities);
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>([]);
    }

    private static readonly PacingStrategyCapabilityResponse DisabledCapabilities = new(false, false, false, false, false, false, 65536, 10, 10);
    private static readonly PacingStrategyCapabilityResponse EnabledCapabilities = new(true, false, false, true, false, false, 65536, 10, 10);

    // Break caught: adding the adjustment shell makes the baseline summary/visualization conditional on adjustments loading.
    [Fact]
    public void Baseline_summary_and_visualization_render_before_and_regardless_of_adjustment_list_state()
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPacingStrategiesAsync = _ => Task.FromException<PacingStrategyCapabilityResponse>(new HttpRequestException("boom"));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        Assert.NotNull(cut.Find("[data-testid=prediction-detail-summary]"));
        Assert.NotNull(cut.Find("[data-testid=prediction-detail-visualization]"));
        Assert.NotNull(cut.Find("[data-testid=adjustment-list-error]"));
    }

    // Break caught: create controls remain visible for an incomplete baseline or a disabled parent capability.
    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    public void Adjustment_controls_are_absent_for_an_incomplete_baseline(string state)
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId) with
        {
            Summary = SucceededPrediction(predictionId).Summary with { State = state }
        });

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=adjustment-builder]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=adjustment-list]"));
    }

    [Fact]
    public void Adjustment_builder_disables_creation_when_the_parent_capability_is_disabled()
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        Assert.NotNull(cut.Find("[data-testid=adjustment-builder-disabled]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=adjustment-builder-available]"));
    }

    [Fact]
    public void Adjustment_builder_lists_available_strategies_when_enabled()
    {
        var predictionId = Guid.NewGuid();
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPacingStrategiesAsync = _ => Task.FromResult(EnabledCapabilities);

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        Assert.Contains("Time target", cut.Find("[data-testid=adjustment-builder-available]").TextContent, StringComparison.Ordinal);
    }

    // Break caught: only the most recently loaded adjustment is retained, or the list collapses to one row.
    [Fact]
    public void List_keeps_every_adjustment_and_each_remains_independently_selectable()
    {
        var predictionId = Guid.NewGuid();
        var first = AdjustmentSummary(predictionId, "TimeTarget");
        var second = AdjustmentSummary(predictionId, "SegmentSpecificGains");
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>([second, first]);
        api.OnGetPredictionAdjustmentAsync = (_, adjustmentId, _) => Task.FromResult<PredictionAdjustmentDetailResponse?>(
            AdjustmentDetail(adjustmentId == first.Id ? first : second));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        var items = cut.FindAll("[data-testid=adjustment-list-items] div.prediction-detail-grid");
        Assert.Equal(2, items.Count);
        Assert.NotNull(cut.Find($"[data-testid=adjustment-card-{first.Id}]"));
        Assert.NotNull(cut.Find($"[data-testid=adjustment-card-{second.Id}]"));
    }

    // Break caught: selecting a second adjustment leaves the first one's comparison rendered alongside it.
    [Fact]
    public void Only_one_adjustment_can_be_compared_at_a_time()
    {
        var predictionId = Guid.NewGuid();
        var first = AdjustmentSummary(predictionId, "TimeTarget");
        var second = AdjustmentSummary(predictionId, "SegmentSpecificGains");
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>([first, second]);
        api.OnGetPredictionAdjustmentAsync = (_, adjustmentId, _) => Task.FromResult<PredictionAdjustmentDetailResponse?>(
            AdjustmentDetail(adjustmentId == first.Id ? first : second));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));
        cut.Find($"[data-testid=adjustment-compare-{first.Id}]").Click();
        Assert.Single(cut.FindAll("[data-testid=adjustment-comparison]"));
        Assert.Contains("Time target", cut.Find("[data-testid=adjustment-comparison-table] thead").TextContent, StringComparison.Ordinal);

        cut.Find($"[data-testid=adjustment-compare-{second.Id}]").Click();
        Assert.Single(cut.FindAll("[data-testid=adjustment-comparison]"));
        Assert.Contains("Segment specific gains", cut.Find("[data-testid=adjustment-comparison-table] thead").TextContent, StringComparison.Ordinal);
    }

    // Break caught: "Back to baseline" deletes the adjustment instead of merely clearing the selection.
    [Fact]
    public void Back_to_baseline_clears_selection_without_deleting_the_adjustment()
    {
        var predictionId = Guid.NewGuid();
        var summary = AdjustmentSummary(predictionId, "TimeTarget");
        var deleteCalled = false;
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>([summary]);
        api.OnGetPredictionAdjustmentAsync = (_, _, _) => Task.FromResult<PredictionAdjustmentDetailResponse?>(AdjustmentDetail(summary));
        api.OnDeletePredictionAdjustmentAsync = (_, _, _) => { deleteCalled = true; return Task.FromResult(true); };

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));
        cut.Find($"[data-testid=adjustment-compare-{summary.Id}]").Click();
        Assert.NotNull(cut.Find("[data-testid=adjustment-comparison]"));

        cut.Find("[data-testid=adjustment-back-to-baseline]").Click();

        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=adjustment-comparison]"));
        Assert.False(deleteCalled);
        Assert.NotNull(cut.Find($"[data-testid=adjustment-card-{summary.Id}]"));
    }

    // Break caught: a failed or cancelled child disappears from the list instead of remaining inspectable.
    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public void Failed_and_cancelled_children_retain_their_row_and_readable_state(string state)
    {
        var predictionId = Guid.NewGuid();
        var summary = AdjustmentSummary(predictionId, "TimeTarget") with { State = state };
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>([summary]);

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        Assert.NotNull(cut.Find($"[data-testid=adjustment-card-{summary.Id}]"));
        Assert.Contains(state, cut.Find($"[data-testid=adjustment-card-state-{summary.Id}]").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // Break caught: deleting the selected child leaves it selected, or removes a sibling instead.
    [Fact]
    public void Deleting_the_selected_child_returns_to_baseline_and_leaves_siblings()
    {
        var predictionId = Guid.NewGuid();
        var deleted = AdjustmentSummary(predictionId, "TimeTarget");
        var sibling = AdjustmentSummary(predictionId, "SegmentSpecificGains");
        var remaining = new List<PredictionAdjustmentSummaryResponse> { deleted, sibling };
        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>(remaining.ToList());
        api.OnGetPredictionAdjustmentAsync = (_, adjustmentId, _) => Task.FromResult<PredictionAdjustmentDetailResponse?>(
            AdjustmentDetail(adjustmentId == deleted.Id ? deleted : sibling));
        api.OnDeletePredictionAdjustmentAsync = (_, adjustmentId, _) =>
        {
            remaining.RemoveAll(item => item.Id == adjustmentId);
            return Task.FromResult(true);
        };

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));
        cut.Find($"[data-testid=adjustment-compare-{deleted.Id}]").Click();
        Assert.NotNull(cut.Find("[data-testid=adjustment-comparison]"));

        cut.Find($"[data-testid=adjustment-delete-{deleted.Id}]").Click();

        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=adjustment-comparison]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find($"[data-testid=adjustment-card-{deleted.Id}]"));
        Assert.NotNull(cut.Find($"[data-testid=adjustment-card-{sibling.Id}]"));
    }

    // Break caught: the visualization keeps comparing a previously selected adjustment, or keeps
    // comparing at all once the selection is cleared.
    [Fact]
    public void Visualization_compares_only_the_selected_adjustment_and_returns_to_baseline_when_cleared()
    {
        var predictionId = Guid.NewGuid();
        var first = AdjustmentSummary(predictionId, "TimeTarget");
        var second = AdjustmentSummary(predictionId, "SegmentSpecificGains");
        IReadOnlyList<PredictionAdjustmentSegmentResponse> firstSegments =
            [new PredictionAdjustmentSegmentResponse(1, 210, 6.5, 70, 70, "Medium", null, null, null)];
        IReadOnlyList<PredictionAdjustmentSegmentResponse> secondSegments =
            [new PredictionAdjustmentSegmentResponse(1, 300, 9.0, 50, 50, "High", null, null, null)];

        api.OnGetPredictionAsync = (_, _) => Task.FromResult<PredictionDetailResponse?>(SucceededPrediction(predictionId));
        api.OnGetPredictionAdjustmentsAsync = (_, _) => Task.FromResult<IReadOnlyList<PredictionAdjustmentSummaryResponse>>([first, second]);
        api.OnGetPredictionAdjustmentAsync = (_, adjustmentId, _) => Task.FromResult<PredictionAdjustmentDetailResponse?>(
            adjustmentId == first.Id
                ? AdjustmentDetail(first, firstSegments)
                : AdjustmentDetail(second, secondSegments));

        var cut = Render<PredictionDetail>(parameters => parameters.Add(page => page.Id, predictionId));

        cut.Find($"[data-testid=adjustment-compare-{first.Id}]").Click();
        var comparison = cut.Find("[data-testid=prediction-visualization-comparison]").TextContent;
        Assert.Contains("245 W", comparison, StringComparison.Ordinal);
        Assert.Contains("210 W", comparison, StringComparison.Ordinal);
        Assert.DoesNotContain("300 W", comparison, StringComparison.Ordinal);

        cut.Find($"[data-testid=adjustment-compare-{second.Id}]").Click();
        comparison = cut.Find("[data-testid=prediction-visualization-comparison]").TextContent;
        Assert.Contains("300 W", comparison, StringComparison.Ordinal);
        Assert.DoesNotContain("210 W", comparison, StringComparison.Ordinal);

        cut.Find("[data-testid=adjustment-back-to-baseline]").Click();

        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=prediction-visualization-comparison]"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find("[data-testid=prediction-visualization-comparison-problem]"));
    }

    private static PredictionAdjustmentSummaryResponse AdjustmentSummary(Guid predictionId, string strategyType) => new(
        Guid.NewGuid(), predictionId, strategyType, "Succeeded", 1100, 6.5, 210, "Medium", [], $"{strategyType}-v1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static PredictionAdjustmentDetailResponse AdjustmentDetail(PredictionAdjustmentSummaryResponse summary) => new(
        summary, JsonDocument.Parse("{}").RootElement, null, []);

    private static PredictionAdjustmentDetailResponse AdjustmentDetail(
        PredictionAdjustmentSummaryResponse summary,
        IReadOnlyList<PredictionAdjustmentSegmentResponse> segments) => new(
        summary, JsonDocument.Parse("{}").RootElement, null, segments);

    private static PredictionDetailResponse SucceededPrediction(Guid predictionId) => new(
        new PredictionSummaryResponse(
            predictionId, "Succeeded", 28750, 420, 3610, 7.96, 245.2, "High", [], Guid.NewGuid(), "v1.0.0", true,
            "Validated", 0.082, 0.156, 75, 10, "dry-road", "calm", "temperate", true,
            DateTimeOffset.Parse("2026-08-25T08:30:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-25T10:01:00Z", CultureInfo.InvariantCulture)),
        [new PredictionSegmentResponse(1, 51.5, -0.12, 100, 500, 500, 0.02, 0.001, 245, 7.9, 62, 62, "High")]);
}
