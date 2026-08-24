namespace RouteTimer.Domain.Models;

public sealed record ModelValidationSummary(ModelValidationStatus Status, double? MedianAbsolutePercentageError, double? P90AbsolutePercentageError);
