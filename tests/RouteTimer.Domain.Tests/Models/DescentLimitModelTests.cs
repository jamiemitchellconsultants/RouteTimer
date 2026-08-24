using RouteTimer.Domain.Models;

namespace RouteTimer.Domain.Tests.Models;

public sealed class DescentLimitModelTests
{
    public static TheoryData<DescentLimitCell> ContradictoryCells => new()
    {
        // Fallback confidence/provenance contradictions.
        { ConservativeCell() with { Confidence = ConfidenceLevel.Medium } },
        { ConservativeCell() with { Confidence = ConfidenceLevel.High } },
        { ConservativeCell() with { Evidence = TimeSpan.FromMinutes(5), ActivityCount = 2 } },
        { ConservativeCell() with { SpeedCapMetresPerSecond = 13.01 } },

        // Learned coverage/confidence contradictions.
        { ConservativeCell() with { Evidence = TimeSpan.FromMinutes(4.99), ActivityCount = 2, Confidence = ConfidenceLevel.Medium, IsFallback = false } },
        { ConservativeCell() with { Evidence = TimeSpan.FromMinutes(5), ActivityCount = 1, Confidence = ConfidenceLevel.Medium, IsFallback = false } },
        { ConservativeCell() with { Evidence = TimeSpan.FromMinutes(5), ActivityCount = 2, Confidence = ConfidenceLevel.Low, IsFallback = false } },
        { ConservativeCell() with { Evidence = TimeSpan.FromMinutes(5), ActivityCount = 2, Confidence = ConfidenceLevel.High, IsFallback = false } },
        { ConservativeCell() with { Evidence = TimeSpan.FromMinutes(20), ActivityCount = 3, Confidence = ConfidenceLevel.Medium, IsFallback = false } },
    };

    // Break caught: descent cells can claim fallback/learned provenance that contradicts their evidence and confidence.
    [Theory]
    [MemberData(nameof(ContradictoryCells))]
    public void Constructor_rejects_cross_field_contradictions(DescentLimitCell contradictory)
    {
        var cells = DescentLimitModel.Conservative.Cells.ToArray();
        cells[0] = contradictory;

        Assert.Throws<ArgumentException>(() => new DescentLimitModel(cells));
    }

    [Fact]
    public void Constructor_accepts_sparse_fallback_metadata_and_slower_caps()
    {
        var cells = DescentLimitModel.Conservative.Cells.ToArray();
        cells[0] = cells[0] with
        {
            SpeedCapMetresPerSecond = 8,
            Evidence = TimeSpan.FromMinutes(30),
            ActivityCount = 1,
        };

        var model = new DescentLimitModel(cells);

        Assert.Equal(cells[0], model.Cells[0]);
    }

    [Theory]
    [InlineData(5, 2, ConfidenceLevel.Medium)]
    [InlineData(20, 2, ConfidenceLevel.Medium)]
    [InlineData(5, 3, ConfidenceLevel.Medium)]
    [InlineData(20, 3, ConfidenceLevel.High)]
    public void Constructor_accepts_exact_learned_confidence_for_coverage(
        double evidenceMinutes,
        int activityCount,
        ConfidenceLevel confidence)
    {
        var cells = DescentLimitModel.Conservative.Cells.ToArray();
        cells[0] = cells[0] with
        {
            Evidence = TimeSpan.FromMinutes(evidenceMinutes),
            ActivityCount = activityCount,
            Confidence = confidence,
            IsFallback = false,
        };

        var model = new DescentLimitModel(cells);

        Assert.False(model.Cells[0].IsFallback);
        Assert.Equal(confidence, model.Cells[0].Confidence);
    }

    private static DescentLimitCell ConservativeCell() => DescentLimitModel.Conservative.Cells[0];
}
