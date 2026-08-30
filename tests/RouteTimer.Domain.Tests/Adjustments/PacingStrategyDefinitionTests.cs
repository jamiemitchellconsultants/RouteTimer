using RouteTimer.Domain.Adjustments;

namespace RouteTimer.Domain.Tests.Adjustments;

public sealed class PacingStrategyDefinitionTests
{
    // Break caught: the strategy union drifts from the five stable discriminators approved in the design.
    [Fact]
    public void PacingStrategyType_declares_exactly_the_five_stable_discriminators()
    {
        var values = Enum.GetValues<PacingStrategyType>();

        Assert.Equal(
        [
            PacingStrategyType.SegmentSpecificGains,
            PacingStrategyType.NpIfTarget,
            PacingStrategyType.TimeTarget,
            PacingStrategyType.RpeZoneShift,
            PacingStrategyType.VariableMatchBurning,
        ], values);
    }

    // Break caught: a concrete definition loses its declared strategy type.
    [Fact]
    public void PacingStrategyDefinition_subtype_carries_its_declared_type()
    {
        var definition = new TestDefinition(PacingStrategyType.TimeTarget);

        Assert.Equal(PacingStrategyType.TimeTarget, definition.Type);
    }

    // Break caught: AdjustmentState drifts from the five job-lifecycle states shared with AnalysisJob.
    [Fact]
    public void AdjustmentState_declares_exactly_the_five_lifecycle_states()
    {
        var values = Enum.GetValues<AdjustmentState>();

        Assert.Equal(
        [
            AdjustmentState.Queued,
            AdjustmentState.Running,
            AdjustmentState.Succeeded,
            AdjustmentState.Failed,
            AdjustmentState.Cancelled,
        ], values);
    }

    // Break caught: the closed adjustment-warning catalog silently accepts an unlisted or baseline warning code.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-warning")]
    [InlineData("power-model-extrapolation")] // a baseline PredictionWarningCodes value, not an adjustment code
    public void AdjustmentWarningCodes_rejects_unknown_or_baseline_codes(string? code)
    {
        Assert.False(AdjustmentWarningCodes.IsKnown(code));
    }

    [Theory]
    [MemberData(nameof(KnownWarningCodes))]
    public void AdjustmentWarningCodes_accepts_every_cataloged_code(string code)
    {
        Assert.True(AdjustmentWarningCodes.IsKnown(code));
    }

    public static TheoryData<string> KnownWarningCodes()
    {
        var data = new TheoryData<string>();
        foreach (var code in AdjustmentWarningCodes.All) data.Add(code);
        return data;
    }

    // Break caught: an annotation loses one of its independently-optional per-segment values.
    [Fact]
    public void PredictionAdjustmentAnnotation_carries_all_three_independently_optional_values()
    {
        var annotation = new PredictionAdjustmentAnnotation(3, "burn", 12345.6);

        Assert.Equal(3, annotation.ZoneNumber);
        Assert.Equal("burn", annotation.StrategyPhase);
        Assert.Equal(12345.6, annotation.WPrimeBalanceJoules);

        var empty = new PredictionAdjustmentAnnotation(null, null, null);
        Assert.Null(empty.ZoneNumber);
        Assert.Null(empty.StrategyPhase);
        Assert.Null(empty.WPrimeBalanceJoules);
    }

    private sealed record TestDefinition(PacingStrategyType Type) : PacingStrategyDefinition(Type);
}
