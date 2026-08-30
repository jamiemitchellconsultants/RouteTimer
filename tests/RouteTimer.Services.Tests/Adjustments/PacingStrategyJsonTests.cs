using RouteTimer.Domain.Adjustments;
using RouteTimer.Services.Adjustments;

namespace RouteTimer.Services.Tests.Adjustments;

public sealed class PacingStrategyJsonTests
{
    // Break caught: canonical JSON uses PascalCase property names instead of deterministic camelCase.
    [Fact]
    public void Canonicalize_uses_camel_case_property_names()
    {
        var json = PacingStrategyJson.Canonicalize(new TestDefinition(PacingStrategyType.TimeTarget, 42, "label"));

        Assert.Contains("\"value\":42", json);
        Assert.Contains("\"label\":\"label\"", json);
        Assert.DoesNotContain("\"Value\"", json);
        Assert.DoesNotContain("\"Label\"", json);
    }

    // Break caught: the enum discriminator serializes as a raw number instead of an explicit string.
    [Fact]
    public void Canonicalize_serializes_enums_as_explicit_camel_case_strings()
    {
        var json = PacingStrategyJson.Canonicalize(new TestDefinition(PacingStrategyType.RpeZoneShift, 1, "x"));

        Assert.Contains("\"type\":\"rpeZoneShift\"", json);
    }

    // Break caught: canonical JSON is pretty-printed, so two semantically identical definitions serialize to different byte strings.
    [Fact]
    public void Canonicalize_never_indents_the_output()
    {
        var json = PacingStrategyJson.Canonicalize(new TestDefinition(PacingStrategyType.SegmentSpecificGains, 1, "x"));

        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain("  ", json);
    }

    // Break caught: a non-finite value silently serializes as a named literal instead of failing canonicalization.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Canonicalize_rejects_non_finite_values(double value)
    {
        var exception = Assert.Throws<PacingStrategyValidationException>(() =>
            PacingStrategyJson.Canonicalize(new TestDefinition(PacingStrategyType.NpIfTarget, value, "x")));

        Assert.Equal("pacing-strategy-invalid", exception.Code);
    }

    // Break caught: a definition larger than 64 KiB after canonicalization is persisted anyway.
    [Fact]
    public void Canonicalize_rejects_definitions_exceeding_the_64_kib_limit()
    {
        var oversized = new string('a', PacingStrategyJson.MaximumBytes);

        var exception = Assert.Throws<PacingStrategyValidationException>(() =>
            PacingStrategyJson.Canonicalize(new TestDefinition(PacingStrategyType.NpIfTarget, 1, oversized)));

        Assert.Equal("pacing-strategy-too-large", exception.Code);
    }

    // Break caught: canonicalizing then deserializing a definition does not reproduce an equal value.
    [Fact]
    public void Canonicalize_and_deserialize_round_trip_the_exact_concrete_subtype()
    {
        var original = new TestDefinition(PacingStrategyType.VariableMatchBurning, 7, "round-trip");

        var json = PacingStrategyJson.Canonicalize(original);
        var restored = PacingStrategyJson.Deserialize<TestDefinition>(json);

        Assert.Equal(original, restored);
    }

    // Break caught: malformed JSON leaks a raw JsonException instead of a stable validation failure.
    [Theory]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"type\":\"variableMatchBurning\",\"value\":\"not-a-number\"}")]
    public void Deserialize_rejects_malformed_or_incomplete_json(string json)
    {
        var exception = Assert.Throws<PacingStrategyValidationException>(() => PacingStrategyJson.Deserialize<TestDefinition>(json));

        Assert.Equal("pacing-strategy-invalid", exception.Code);
    }

    // Break caught: stored JSON is parsed before its size is checked, so an oversized row costs a full
    // parse on the worker before it is rejected.
    [Fact]
    public void Deserialize_rejects_stored_json_above_the_utf8_limit_before_parsing()
    {
        var oversized = new string('x', PacingStrategyJson.MaximumBytes + 1);

        var exception = Assert.Throws<PacingStrategyValidationException>(() =>
            PacingStrategyJson.Deserialize<TestDefinition>(oversized));

        Assert.Equal("pacing-strategy-too-large", exception.Code);
    }

    // Break caught: the limit is measured in UTF-16 chars, so a multibyte payload stores well past
    // 64 KiB of actual bytes.
    [Fact]
    public void The_size_limit_counts_utf8_bytes_not_characters()
    {
        // Three UTF-8 bytes per character, so half the character budget is already over the limit.
        var multibyte = new string('\u4e2d', (PacingStrategyJson.MaximumBytes / 2) + 1);
        Assert.True(multibyte.Length < PacingStrategyJson.MaximumBytes);

        var canonicalize = Assert.Throws<PacingStrategyValidationException>(() =>
            PacingStrategyJson.Canonicalize(new TestDefinition(PacingStrategyType.NpIfTarget, 1, multibyte)));
        var deserialize = Assert.Throws<PacingStrategyValidationException>(() =>
            PacingStrategyJson.Deserialize<TestDefinition>(multibyte));

        Assert.Equal("pacing-strategy-too-large", canonicalize.Code);
        Assert.Equal("pacing-strategy-too-large", deserialize.Code);
    }

    // Break caught: JSON that parses but fails the domain constructor leaks a raw ArgumentException
    // out of the worker instead of the stable validation failure.
    [Fact]
    public void Deserialize_translates_constructor_validation_into_a_stable_failure()
    {
        var json = """{"type":"rpeZoneShift","thresholdMode":"ftpBased","ftpWatts":9000,"assignments":[]}""";

        var exception = Assert.Throws<PacingStrategyValidationException>(() =>
            PacingStrategyJson.Deserialize<RouteTimer.Domain.Adjustments.Zones.ZoneShiftDefinition>(json));

        Assert.Equal("pacing-strategy-invalid", exception.Code);
    }

    // Break caught: cancellation is swallowed by the catch-all and reported as malformed JSON.
    [Fact]
    public void Deserialize_does_not_swallow_cancellation()
    {
        Assert.Throws<OperationCanceledException>(() =>
            PacingStrategyJson.Deserialize<TestDefinition>(ThrowingJson()));

        static string ThrowingJson() => throw new OperationCanceledException();
    }

    private sealed record TestDefinition(PacingStrategyType Type, double Value, string Label) : PacingStrategyDefinition(Type);
}
