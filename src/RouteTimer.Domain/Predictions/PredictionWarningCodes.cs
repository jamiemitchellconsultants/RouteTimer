namespace RouteTimer.Domain.Predictions;

public static class PredictionWarningCodes
{
    public const string PowerModelExtrapolation = "power-model-extrapolation";
    public const string ConservativeDescentLimits = "conservative-descent-limits";
    public const string UncalibratedCoefficients = "uncalibrated-coefficients";
    public const string ModelValidationFailed = "model-validation-failed";
    public const string ModelValidationInsufficientData = "model-validation-insufficient-data";
    public const string ModelValidationNotValidated = "model-validation-not-validated";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        PowerModelExtrapolation,
        ConservativeDescentLimits,
        UncalibratedCoefficients,
        ModelValidationFailed,
        ModelValidationInsufficientData,
        ModelValidationNotValidated,
    ]);

    public static bool IsKnown(string? code) =>
        code is not null && All.Contains(code, StringComparer.Ordinal);
}
