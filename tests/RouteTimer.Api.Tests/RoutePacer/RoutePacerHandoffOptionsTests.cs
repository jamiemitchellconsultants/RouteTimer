using System.Security.Cryptography;
using RouteTimer.Api.RoutePacer;

namespace RouteTimer.Api.Tests.RoutePacer;

public sealed class RoutePacerHandoffOptionsTests
{
    // The same test key the frozen contract fixture uses. It signs nothing outside tests.
    internal const string TestPrivateKeyPem =
        "-----BEGIN PRIVATE KEY-----\n" +
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg2Ylwv8R3sYAMK3mj\n" +
        "/BhpxW9UXtZtVEfTJdiHpk26dOWhRANCAAR4X3vPpSA8XG0fN6gDO5/8Ug++WbJB\n" +
        "e1NeWN/zQ0cuvYPSlZNF+WA2orizjvPGtUMgLWeZ/cEMG3A5Fu3pzOqE\n" +
        "-----END PRIVATE KEY-----\n";

    internal const string TestRelayOrigin = "https://pacetracking.tqaentry.com";

    internal static RoutePacerHandoffOptions Enabled() => new()
    {
        Enabled = true,
        RoutePacerBaseUrl = TestRelayOrigin,
        RelayUploadKey = "test-upload-key",
        SigningPrivateKeyPem = TestPrivateKeyPem
    };

    private static bool Validate(RoutePacerHandoffOptions options, out string? failure)
    {
        var result = new RoutePacerHandoffOptionsValidator().Validate(RoutePacerHandoffOptions.SectionName, options);
        failure = result.FailureMessage;
        return result.Succeeded;
    }

    [Fact]
    public void Disabled_configuration_with_empty_secrets_is_valid()
    {
        Assert.True(Validate(new RoutePacerHandoffOptions(), out var failure), failure);
    }

    [Fact]
    public void Enabled_configuration_with_a_valid_key_is_valid()
    {
        Assert.True(Validate(Enabled(), out var failure), failure);
    }

    [Fact]
    public void Enabled_configuration_rejects_an_empty_upload_key()
    {
        Assert.False(Validate(Enabled() with { RelayUploadKey = "  " }, out var failure));
        Assert.Contains("RelayUploadKey", failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_configuration_rejects_an_empty_private_key()
    {
        Assert.False(Validate(Enabled() with { SigningPrivateKeyPem = "" }, out var failure));
        Assert.Contains("SigningPrivateKeyPem", failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_configuration_rejects_an_unparseable_private_key()
    {
        Assert.False(Validate(Enabled() with { SigningPrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nnot-a-key\n-----END PRIVATE KEY-----\n" }, out var failure));
        Assert.Contains("SigningPrivateKeyPem", failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_configuration_rejects_a_non_P256_private_key()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.False(Validate(Enabled() with { SigningPrivateKeyPem = p384.ExportPkcs8PrivateKeyPem() }, out var failure));
        Assert.Contains("SigningPrivateKeyPem", failure!, StringComparison.Ordinal);
    }

    // Validated even while disabled: the origin is published to the browser by the status endpoint
    // whatever the flag says, and an http:// origin there would silently downgrade the QR link.
    [Theory]
    [InlineData("http://pacetracking.tqaentry.com")]
    [InlineData("https://pacetracking.tqaentry.com/relay")]
    [InlineData("https://pacetracking.tqaentry.com/?a=b")]
    [InlineData("https://pacetracking.tqaentry.com/#top")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void Base_url_must_be_a_bare_https_origin(string baseUrl)
    {
        Assert.False(Validate(new RoutePacerHandoffOptions { RoutePacerBaseUrl = baseUrl }, out var failure));
        Assert.Contains("RoutePacerBaseUrl", failure!, StringComparison.Ordinal);
    }

    // The failure text reaches startup logs and crash output, so it must never carry the values.
    [Fact]
    public void Failure_messages_never_repeat_the_configured_secrets()
    {
        Validate(Enabled() with { SigningPrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nbroken\n-----END PRIVATE KEY-----\n" }, out var failure);

        Assert.DoesNotContain("BEGIN PRIVATE KEY", failure!, StringComparison.Ordinal);
        Assert.DoesNotContain("test-upload-key", failure!, StringComparison.Ordinal);
    }
}
