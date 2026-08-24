namespace RouteTimer.Domain.Models;

public sealed record PowerBand(string GradeKey, string DurationKey, double TypicalWatts, TimeSpan Evidence, int ActivityCount, double ShrinkageWeight, ConfidenceLevel Confidence);
public sealed record PowerEstimate(double Watts, ConfidenceLevel Confidence, bool Extrapolated, string Reason);
