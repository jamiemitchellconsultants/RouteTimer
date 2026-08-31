using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Jobs;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Services.Adjustments;
using RouteTimer.Services.Adjustments.TimeTarget;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;
using Xunit;

namespace RouteTimer.Services.Tests.Adjustments;

/// <summary>
/// Adversarial worker-side cases: what the job does with a row it cannot trust. Every failure must be
/// a stable <see cref="PredictionAdjustmentJobException"/> code with nothing published, except
/// cancellation, which stays an <see cref="OperationCanceledException"/> so the queue can requeue it.
/// </summary>
public sealed class PredictionAdjustmentFailureTests
{
    // Break caught: a stored strategy row that no longer parses is reported as a calculation failure,
    // so an operator chasing it looks at the physics instead of the data.
    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("""{"type":"timeTarget","distribution":"sideways"}""")]
    [InlineData("""{"type":"timeTarget","targetMovingSeconds":-1,"distribution":"proportional"}""")]
    public async Task Malformed_or_invalid_stored_strategy_fails_as_a_strategy_problem(string storedJson)
    {
        var harness = new AdjustmentJobHandlerHarness(RealTimeTargetHandler(), storedJson);

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(
            () => harness.HandleAsync());

        Assert.Equal("invalid-prediction-adjustment-strategy", exception.Code);
        Assert.Empty(harness.Adjustments.PublishCalls);
    }

    [Fact]
    public async Task Stored_strategy_above_the_byte_limit_fails_as_a_strategy_problem()
    {
        var oversized = new string('x', PacingStrategyJson.MaximumBytes + 1);
        var harness = new AdjustmentJobHandlerHarness(RealTimeTargetHandler(), oversized);

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(
            () => harness.HandleAsync());

        Assert.Equal("invalid-prediction-adjustment-strategy", exception.Code);
        Assert.Empty(harness.Adjustments.PublishCalls);
    }

    // Break caught: a search that never finds a usable candidate throws something the job boundary
    // does not translate, so the job retries forever instead of failing permanently.
    [Fact]
    public async Task A_search_with_no_valid_candidate_fails_as_a_result_problem()
    {
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget)
        {
            RunException = new ArgumentException("Time-target search produced no valid simulation candidate."),
        };
        var harness = new AdjustmentJobHandlerHarness(handler);

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(
            () => harness.HandleAsync());

        Assert.Equal("invalid-prediction-adjustment-result", exception.Code);
        Assert.Empty(harness.Adjustments.PublishCalls);
    }

    // Break caught: cancellation is translated into a permanent diagnostic, so a job cancelled by a
    // baseline deletion is recorded as broken data.
    [Fact]
    public async Task Cancellation_escapes_the_handler_without_publishing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget)
        {
            RunException = new OperationCanceledException(cancellation.Token),
        };
        var harness = new AdjustmentJobHandlerHarness(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.HandleAsync(cancellation.Token));

        Assert.Empty(harness.Adjustments.PublishCalls);
    }

    // Break caught: an annotation carrying a nonsensical value is persisted, so the chart later renders
    // a negative W-prime balance or a phase no reader can interpret.
    [Theory]
    [InlineData(null, null, double.NaN)]
    [InlineData(null, null, double.PositiveInfinity)]
    [InlineData(null, null, -1d)]
    [InlineData(0, null, null)]
    [InlineData(-3, null, null)]
    [InlineData(null, "sprinting", null)]
    public async Task An_invalid_annotation_fails_as_a_result_problem(int? zone, string? phase, double? wPrime)
    {
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget)
        {
            RunResult = Computation(new Dictionary<int, PredictionAdjustmentAnnotation>
            {
                [1] = new(zone, phase, wPrime),
            }),
        };
        var harness = new AdjustmentJobHandlerHarness(handler);

        var exception = await Assert.ThrowsAsync<PredictionAdjustmentJobException>(
            () => harness.HandleAsync());

        Assert.Equal("invalid-prediction-adjustment-result", exception.Code);
        Assert.Empty(harness.Adjustments.PublishCalls);
    }

    [Theory]
    [InlineData(1, "baseline", 0d)]
    [InlineData(7, "conservation", 12_500d)]
    [InlineData(null, "recovery", null)]
    [InlineData(null, "burn", null)]
    [InlineData(null, null, null)]
    public async Task A_valid_annotation_publishes(int? zone, string? phase, double? wPrime)
    {
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget)
        {
            RunResult = Computation(new Dictionary<int, PredictionAdjustmentAnnotation>
            {
                [1] = new(zone, phase, wPrime),
            }),
        };
        var harness = new AdjustmentJobHandlerHarness(handler);

        await harness.HandleAsync();

        var published = Assert.Single(harness.Adjustments.PublishCalls);
        var segment = published.Publication.Segments.Single();
        Assert.Equal(zone, segment.ZoneNumber);
        Assert.Equal(phase, segment.StrategyPhase);
        Assert.Equal(wPrime, segment.WPrimeBalanceJoules);
    }

    // Break caught: the predictor flags a target it could not hold, but the adjustment publishes
    // without it, so a rider sees a slow result with no explanation. The translation happens once at
    // the publication boundary, so it must apply whichever strategy produced the result.
    [Fact]
    public async Task A_power_limited_replay_publishes_the_strategy_warning()
    {
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget)
        {
            RunResult = new PacingStrategyComputation(
                new PredictionResult(
                    [new PredictionSegment(1, 100, .02, 5, .5, TimeSpan.FromSeconds(200), ConfidenceLevel.Low)],
                    TimeSpan.FromSeconds(200),
                    ConfidenceLevel.Low,
                    [PredictionWarningCodes.PowerBelowSustainableSpeed]),
                new AdjustmentJobHandlerHarness.TestReport(PacingStrategyType.TimeTarget),
                new Dictionary<int, PredictionAdjustmentAnnotation>(),
                [],
                "time-target-v1"),
        };
        var harness = new AdjustmentJobHandlerHarness(handler);

        await harness.HandleAsync();

        var published = Assert.Single(harness.Adjustments.PublishCalls);
        Assert.Contains(AdjustmentWarningCodes.StrategyPowerBelowSustainableSpeed, published.Publication.Warnings);
    }

    [Fact]
    public async Task A_replay_that_held_its_targets_publishes_no_power_warning()
    {
        var handler = new RecordingHandler(PacingStrategyType.TimeTarget)
        {
            RunResult = Computation(new Dictionary<int, PredictionAdjustmentAnnotation>()),
        };
        var harness = new AdjustmentJobHandlerHarness(handler);

        await harness.HandleAsync();

        var published = Assert.Single(harness.Adjustments.PublishCalls);
        Assert.DoesNotContain(AdjustmentWarningCodes.StrategyPowerBelowSustainableSpeed, published.Publication.Warnings);
    }

    private static IPacingStrategyHandler RealTimeTargetHandler() =>
        new TimeTargetHandler(new RoutePredictor(new DescentSpeedLimiter()));

    private static PacingStrategyComputation Computation(Dictionary<int, PredictionAdjustmentAnnotation> annotations) =>
        new(
            new PredictionResult(
                [new PredictionSegment(1, 100, .02, 200, 5, TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)],
                TimeSpan.FromSeconds(20),
                ConfidenceLevel.Medium,
                []),
            new AdjustmentJobHandlerHarness.TestReport(PacingStrategyType.TimeTarget),
            annotations,
            [],
            "time-target-v1");
}
