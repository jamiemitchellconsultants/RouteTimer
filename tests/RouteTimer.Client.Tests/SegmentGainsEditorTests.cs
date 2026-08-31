using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components.Adjustments;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Adjustments;

namespace RouteTimer.Client.Tests;

public sealed class SegmentGainsEditorTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();
    private readonly Guid predictionId = Guid.NewGuid();

    public SegmentGainsEditorTests() => Services.AddSingleton<IRouteTimerApiClient>(api);

    // Break caught: adding rules past the ten-rule limit is possible from the UI.
    [Fact]
    public void Add_rule_is_disabled_once_ten_rules_exist()
    {
        var cut = Render<SegmentGainsEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));

        for (var index = 0; index < 10; index++)
        {
            cut.Find("[data-testid=segment-gains-add-rule]").Click();
        }

        Assert.Equal(10, cut.FindAll("div.prediction-detail-grid").Count);
        Assert.True(cut.Find("[data-testid=segment-gains-add-rule]").HasAttribute("disabled"));
    }

    // Break caught: removing a rule removes the wrong row or leaves a stale one behind.
    [Fact]
    public void Remove_rule_removes_only_that_row()
    {
        var cut = Render<SegmentGainsEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=segment-gains-add-rule]").Click();
        cut.Find("[data-testid=segment-gains-add-rule]").Click();
        cut.Find("[data-testid=segment-gains-min-1]").Input("0.05");

        cut.Find("[data-testid=segment-gains-remove-0]").Click();

        Assert.Single(cut.FindAll("[data-testid=segment-gains-rule-0]"));
        Assert.Equal("0.05", cut.Find("[data-testid=segment-gains-min-0]").GetAttribute("value"));
    }

    // Break caught: switching a rule's selector leaves the old selector's bounds set, silently combining two selectors.
    [Fact]
    public void Switching_selector_clears_the_old_bounds()
    {
        var cut = Render<SegmentGainsEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=segment-gains-add-rule]").Click();
        cut.Find("[data-testid=segment-gains-min-0]").Input("0.02");

        cut.Find("[data-testid=segment-gains-selector-0]").Change("Sequence");

        Assert.Null(cut.Find("[data-testid=segment-gains-min-0]").GetAttribute("value"));
    }

    // Break caught: switching from factor to delta (or back) leaves the old mode's value set, submitting both.
    [Fact]
    public void Switching_mode_clears_the_old_values_value()
    {
        var cut = Render<SegmentGainsEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=segment-gains-add-rule]").Click();
        cut.Find("[data-testid=segment-gains-value-0]").Input("1.5");

        cut.Find("[data-testid=segment-gains-mode-0]").Change("Delta");

        Assert.Null(cut.Find("[data-testid=segment-gains-value-0]").GetAttribute("value"));
    }

    // Break caught: submitting builds a malformed request, or the caller is never told a new adjustment was created.
    [Fact]
    public void Submit_sends_the_configured_rules_and_notifies_the_caller()
    {
        SegmentSpecificGainsRequest? captured = null;
        var createdAdjustmentId = Guid.NewGuid();
        api.OnCreatePredictionAdjustmentAsync = (id, request, _) =>
        {
            captured = Assert.IsType<SegmentSpecificGainsRequest>(request);
            return Task.FromResult(new PredictionAdjustmentSubmissionResponse(createdAdjustmentId, Guid.NewGuid(), id));
        };
        PredictionAdjustmentSubmissionResponse? notified = null;

        var cut = Render<SegmentGainsEditor>(parameters => parameters
            .Add(editor => editor.PredictionId, predictionId)
            .Add(editor => editor.OnCreated, id => notified = id));
        cut.Find("[data-testid=segment-gains-add-rule]").Click();
        cut.Find("[data-testid=segment-gains-min-0]").Input("0.02");
        cut.Find("[data-testid=segment-gains-value-0]").Input("1.2");

        cut.Find("[data-testid=segment-gains-submit]").Click();

        Assert.NotNull(captured);
        var rule = Assert.Single(captured.Rules);
        Assert.Equal(.02, rule.MinGradient);
        Assert.Equal(1.2, rule.Factor);
        Assert.Null(rule.DeltaWatts);
        Assert.Equal(createdAdjustmentId, notified?.AdjustmentId);
    }

    // Break caught: a server field error for one rule is rendered against every rule, or none at all.
    [Fact]
    public void Server_field_errors_render_next_to_the_owning_rule()
    {
        api.OnCreatePredictionAdjustmentAsync = (_, _, _) => throw new ApiProblemException(
            System.Net.HttpStatusCode.BadRequest, "pacing-strategy-invalid", "Invalid", "detail",
            new Dictionary<string, string[]> { ["rules[0]"] = ["A segment gains rule requires exactly one of factor or delta watts."] });

        var cut = Render<SegmentGainsEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=segment-gains-add-rule]").Click();
        cut.Find("[data-testid=segment-gains-min-0]").Input("0.02");

        cut.Find("[data-testid=segment-gains-submit]").Click();

        Assert.Contains("exactly one of factor or delta", cut.Find("[data-testid=segment-gains-rule-error-0]").TextContent);
    }
}
