using RouteTimer.Domain.Models;

namespace RouteTimer.Domain.Predictions;

public sealed record PredictionResult
{
    private IReadOnlyList<PredictionSegment> _segments = null!;
    private IReadOnlyList<string> _warnings = null!;

    public PredictionResult(
        IReadOnlyList<PredictionSegment> Segments,
        TimeSpan MovingTime,
        ConfidenceLevel Confidence,
        IReadOnlyList<string> Warnings)
    {
        this.Segments = Segments;
        this.MovingTime = MovingTime;
        this.Confidence = Confidence;
        this.Warnings = Warnings;
    }

    public IReadOnlyList<PredictionSegment> Segments
    {
        get => _segments;
        init => _segments = value is null ? null! : Array.AsReadOnly(value.ToArray());
    }

    public TimeSpan MovingTime { get; init; }
    public ConfidenceLevel Confidence { get; init; }

    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        init => _warnings = value is null ? null! : Array.AsReadOnly(value.ToArray());
    }

    public void Deconstruct(
        out IReadOnlyList<PredictionSegment> segments,
        out TimeSpan movingTime,
        out ConfidenceLevel confidence,
        out IReadOnlyList<string> warnings)
    {
        segments = Segments;
        movingTime = MovingTime;
        confidence = Confidence;
        warnings = Warnings;
    }
}
