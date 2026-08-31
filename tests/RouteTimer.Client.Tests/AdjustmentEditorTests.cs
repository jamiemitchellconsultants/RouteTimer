using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Client.Api;
using RouteTimer.Client.Components.Adjustments;
using RouteTimer.Client.Tests.Fakes;
using RouteTimer.Contracts.Adjustments;

namespace RouteTimer.Client.Tests;

/// <summary>
/// Behaviour of the NP/IF, time-target, and match-burning editors. Segment gains and zone shift have
/// their own files; every editor's presence in the builder is covered by <see cref="AdjustmentBuilderTests"/>.
/// </summary>
public sealed class AdjustmentEditorTests : BunitContext
{
    private readonly FakeRouteTimerApiClient api = new();
    private readonly Guid predictionId = Guid.NewGuid();

    public AdjustmentEditorTests() => Services.AddSingleton<IRouteTimerApiClient>(api);

    // Break caught: an out-of-range intensity factor or FTP is submitted and bounced by the server
    // instead of being caught while the field is still in front of the rider.
    [Theory]
    [InlineData("0.85", "250", false)]
    [InlineData("1.50", "250", false)]
    [InlineData("1.51", "250", true)]
    [InlineData("0", "250", true)]
    [InlineData("0.85", "", true)]
    [InlineData("0.85", "2001", true)]
    [InlineData("not a number", "250", true)]
    public void NpIf_submit_is_disabled_outside_the_documented_bounds(string targetIf, string ftp, bool expectDisabled)
    {
        var cut = Render<NpIfTargetEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=np-if-target]").Input(targetIf);
        cut.Find("[data-testid=np-if-ftp]").Input(ftp);

        Assert.Equal(expectDisabled, cut.Find("[data-testid=np-if-submit]").HasAttribute("disabled"));
    }

    [Fact]
    public void NpIf_submits_the_entered_target_ftp_and_mode()
    {
        NpIfTargetRequest? captured = null;
        var createdId = Guid.NewGuid();
        api.OnCreatePredictionAdjustmentAsync = (_, request, _) =>
        {
            captured = Assert.IsType<NpIfTargetRequest>(request);
            return Task.FromResult(new PredictionAdjustmentSubmissionResponse(createdId, Guid.NewGuid(), predictionId));
        };
        PredictionAdjustmentSubmissionResponse? notified = null;

        var cut = Render<NpIfTargetEditor>(parameters => parameters
            .Add(editor => editor.PredictionId, predictionId)
            .Add(editor => editor.OnCreated, id => notified = id));
        cut.Find("[data-testid=np-if-target]").Input("0.92");
        cut.Find("[data-testid=np-if-ftp]").Input("275");
        cut.Find("[data-testid=np-if-mode]").Change("additive");

        cut.Find("[data-testid=np-if-submit]").Click();

        Assert.NotNull(captured);
        Assert.Equal(0.92, captured.TargetIntensityFactor);
        Assert.Equal(275, captured.FtpWatts);
        Assert.Equal("additive", captured.Mode);
        Assert.Equal(createdId, notified?.AdjustmentId);
    }

    [Fact]
    public void NpIf_renders_a_server_problem_without_losing_the_form()
    {
        api.OnCreatePredictionAdjustmentAsync = (_, _, _) => throw new ApiProblemException(
            System.Net.HttpStatusCode.BadRequest, "pacing-strategy-invalid", "Invalid", "The strategy is disabled.");

        var cut = Render<NpIfTargetEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=np-if-ftp]").Input("250");

        cut.Find("[data-testid=np-if-submit]").Click();

        Assert.NotNull(cut.Find("[data-testid=np-if-error]"));
        Assert.False(cut.Find("[data-testid=np-if-submit]").HasAttribute("disabled"));
    }

    // Break caught: the duration field accepts something the server's [1, 172800] range rejects, or
    // rejects a boundary it accepts.
    [Theory]
    [InlineData("01:00:00", false)]
    [InlineData("00:00:01", false)]
    [InlineData("48:00:00", false)]
    [InlineData("00:00:00", true)]
    [InlineData("48:00:01", true)]
    [InlineData("1:00:00", true)]
    [InlineData("01:60:00", true)]
    [InlineData("banana", true)]
    public void TimeTarget_validates_the_duration_before_submitting(string duration, bool expectError)
    {
        var cut = Render<TimeTargetEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=time-target-duration]").Input(duration);

        Assert.Equal(expectError, cut.Find("[data-testid=time-target-submit]").HasAttribute("disabled"));
    }

    [Fact]
    public void TimeTarget_renders_a_six_step_climb_focus_scale()
    {
        var cut = Render<TimeTargetEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));

        var scale = cut.Find("[data-testid=time-target-climb-focus]");

        Assert.Equal("range", scale.GetAttribute("type"));
        Assert.Equal("0", scale.GetAttribute("min"));
        Assert.Equal("5", scale.GetAttribute("max"));
        Assert.Equal("1", scale.GetAttribute("step"));
        Assert.Contains("Proportional", cut.Find("[data-testid=time-target-climb-focus-scale]").TextContent);
        Assert.Contains("Climb focused", cut.Find("[data-testid=time-target-climb-focus-scale]").TextContent);
    }

    // Break caught: a scale level is translated to the wrong distribution or climb bias, so the
    // algorithm applies a different amount of climb focus from the value shown to the rider.
    [Theory]
    [InlineData("0", "proportional", null)]
    [InlineData("1", "climb-focused", 1.2)]
    [InlineData("2", "climb-focused", 1.4)]
    [InlineData("3", "climb-focused", 1.6)]
    [InlineData("4", "climb-focused", 1.8)]
    [InlineData("5", "climb-focused", 2.0)]
    public void TimeTarget_maps_climb_focus_level_to_the_submitted_bias(
        string level,
        string expectedDistribution,
        double? expectedClimbBias)
    {
        TimeTargetRequest? captured = null;
        api.OnCreatePredictionAdjustmentAsync = (_, request, _) =>
        {
            captured = Assert.IsType<TimeTargetRequest>(request);
            return Task.FromResult(new PredictionAdjustmentSubmissionResponse(Guid.NewGuid(), Guid.NewGuid(), predictionId));
        };

        var cut = Render<TimeTargetEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=time-target-duration]").Input("02:30:45");
        cut.Find("[data-testid=time-target-climb-focus]").Input(level);

        cut.Find("[data-testid=time-target-submit]").Click();

        Assert.NotNull(captured);
        Assert.Equal(9045, captured.TargetMovingSeconds);
        Assert.Equal(expectedDistribution, captured.Distribution);
        Assert.Equal(expectedClimbBias, captured.ClimbBias);
    }

    [Fact]
    public void MatchBurning_add_window_stops_at_the_ten_window_limit()
    {
        var cut = Render<MatchBurningEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));

        for (var index = 0; index < 12; index++)
        {
            var add = cut.Find("[data-testid=match-burning-add-window]");
            if (add.HasAttribute("disabled")) break;
            add.Click();
        }

        Assert.Equal(10, cut.FindAll("[data-testid^=match-burning-window-]").Count);
        Assert.True(cut.Find("[data-testid=match-burning-add-window]").HasAttribute("disabled"));
    }

    // Break caught: the selector chooses which bound fields are sent, so a window switched from
    // gradient to sequence carries the old gradient value into the request.
    [Fact]
    public void MatchBurning_sends_only_the_bounds_belonging_to_the_selected_selector()
    {
        VariableMatchBurningRequest? captured = null;
        api.OnCreatePredictionAdjustmentAsync = (_, request, _) =>
        {
            captured = Assert.IsType<VariableMatchBurningRequest>(request);
            return Task.FromResult(new PredictionAdjustmentSubmissionResponse(Guid.NewGuid(), Guid.NewGuid(), predictionId));
        };

        var cut = Render<MatchBurningEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=match-burning-min-0]").Input("0.04");
        cut.Find("[data-testid=match-burning-selector-0]").Change("sequence");
        cut.Find("[data-testid=match-burning-min-0]").Input("5");
        cut.Find("[data-testid=match-burning-max-0]").Input("9");

        cut.Find("[data-testid=match-burning-submit]").Click();

        Assert.NotNull(captured);
        var window = Assert.Single(captured.Windows);
        Assert.Equal("sequence", window.Selector);
        Assert.Equal(5, window.MinSequence);
        Assert.Equal(9, window.MaxSequence);
        Assert.Null(window.MinGradient);
        Assert.Null(window.MaxGradient);
        Assert.Null(window.MinDistanceMetres);
    }

    // Break caught: text that cannot be parsed is silently replaced by the field's default, so a
    // mistyped conservation duration computes the adjustment from a number nobody entered.
    [Theory]
    [InlineData("match-burning-conservation-duration", "12O")]
    [InlineData("match-burning-conservation-fraction", "point eight")]
    [InlineData("match-burning-recovery-duration", "3OO")]
    [InlineData("match-burning-recovery-fraction", "~0.7")]
    [InlineData("match-burning-cp", "two fifty")]
    [InlineData("match-burning-wprime", "20k")]
    public void MatchBurning_submit_is_disabled_for_unparseable_input(string testId, string typo)
    {
        var cut = Render<MatchBurningEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        Assert.False(cut.Find("[data-testid=match-burning-submit]").HasAttribute("disabled"));

        cut.Find($"[data-testid={testId}]").Input(typo);

        Assert.True(cut.Find("[data-testid=match-burning-submit]").HasAttribute("disabled"));
    }

    // Break caught: a mistyped gradient bound parses as null and submits an unbounded rule instead.
    [Fact]
    public void ZoneShift_submit_is_disabled_for_an_unparseable_gradient_bound()
    {
        var cut = Render<ZoneShiftEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=zone-shift-add-assignment]").Click();
        cut.Find("[data-testid=zone-shift-min-1]").Input("0.05");
        Assert.False(cut.Find("[data-testid=zone-shift-submit]").HasAttribute("disabled"));

        cut.Find("[data-testid=zone-shift-max-1]").Input("O.1");

        Assert.True(cut.Find("[data-testid=zone-shift-submit]").HasAttribute("disabled"));
    }

    [Fact]
    public void MatchBurning_leaves_capacity_blank_so_the_server_infers_it()
    {
        VariableMatchBurningRequest? captured = null;
        api.OnCreatePredictionAdjustmentAsync = (_, request, _) =>
        {
            captured = Assert.IsType<VariableMatchBurningRequest>(request);
            return Task.FromResult(new PredictionAdjustmentSubmissionResponse(Guid.NewGuid(), Guid.NewGuid(), predictionId));
        };

        var cut = Render<MatchBurningEditor>(parameters => parameters.Add(editor => editor.PredictionId, predictionId));
        cut.Find("[data-testid=match-burning-submit]").Click();

        Assert.NotNull(captured);
        Assert.Null(captured.CriticalPowerWatts);
        Assert.Null(captured.WPrimeJoules);
        Assert.Equal(120, captured.ConservationDurationSeconds);
        Assert.Equal(0.80, captured.ConservationTargetCpFraction);
        Assert.Equal(300, captured.RecoveryDurationSeconds);
        Assert.Equal(0.70, captured.RecoveryTargetCpFraction);
    }
}
