namespace RouteTimer.Services.Adjustments;

/// <summary>
/// A pacing-adjustment strategy definition failed validation before it could be canonicalized or
/// persisted (malformed JSON, unknown discriminator, invalid range, or a limit violation). The API
/// boundary maps <see cref="Code"/> to a stable Problem Details code and a 400 response.
/// </summary>
public sealed class PacingStrategyValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
