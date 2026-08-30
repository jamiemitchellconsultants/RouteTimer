using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteTimer.Domain.Adjustments;

namespace RouteTimer.Services.Adjustments;

/// <summary>
/// Canonical JSON for pacing-adjustment strategy definitions and reports: deterministic camelCase
/// property names, explicit enum strings, no indentation, and (left at the serializer default)
/// rejection of named floating-point literals such as NaN or Infinity. Every definition is
/// round-tripped through its expected concrete subtype before persisting, and the canonical bytes
/// are checked against the 64 KiB limit shared by every strategy.
/// </summary>
public static class PacingStrategyJson
{
    public const int MaximumBytes = 65536;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Serializes <paramref name="value"/> to canonical JSON and validates its UTF-8 byte count.
    /// Callers use their strategy's own concrete subtype as <typeparamref name="T"/> so the output
    /// carries exactly that subtype's properties.
    /// </summary>
    public static string Canonicalize<T>(T value)
        where T : PacingStrategyDefinition
    {
        ArgumentNullException.ThrowIfNull(value);
        string json;
        try
        {
            json = JsonSerializer.Serialize(value, Options);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new PacingStrategyValidationException("pacing-strategy-invalid", "Pacing strategy definition contains a non-finite value.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
        {
            throw new PacingStrategyValidationException("pacing-strategy-too-large", $"Pacing strategy definition exceeds {MaximumBytes} bytes.");
        }

        return json;
    }

    /// <summary>
    /// Deserializes canonical JSON back to its expected concrete subtype, translating any malformed
    /// or structurally invalid input into <see cref="PacingStrategyValidationException"/>.
    /// </summary>
    public static T Deserialize<T>(string json)
        where T : PacingStrategyDefinition
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new PacingStrategyValidationException("pacing-strategy-invalid", "Pacing strategy definition is malformed.");
        }
        catch (JsonException)
        {
            throw new PacingStrategyValidationException("pacing-strategy-invalid", "Pacing strategy definition is malformed.");
        }
    }

    /// <summary>
    /// Serializes a strategy's report to canonical JSON, mirroring <see cref="Canonicalize{T}"/> but for
    /// <see cref="PacingStrategyReport"/> subtypes. Reports are handler-produced, not user-submitted, so
    /// no byte-size limit is enforced here.
    /// </summary>
    public static string CanonicalizeReport<T>(T value)
        where T : PacingStrategyReport
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return JsonSerializer.Serialize(value, Options);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new PacingStrategyValidationException("pacing-strategy-invalid", "Pacing strategy report contains a non-finite value.");
        }
    }
}
