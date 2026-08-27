using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteTimer.Services.RoutePacer;

namespace RouteTimer.Services.Tests.RoutePacer;

// The fixture is the cross-repository interop vector: RoutePacer holds a byte-identical copy, so
// every value here is compared exactly rather than recomputed. The one exception is the signature
// itself. ECDSA signing draws a fresh random nonce per call, so a freshly produced signature is
// never byte-equal to the recorded one; it is verified against the fixture's public key instead,
// and the recorded signature is what proves RoutePacer's verifier and this signer agree.
public sealed class RoutePacerContractTests
{
    private sealed record ContractFixture(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("privateKeyPem")] string PrivateKeyPem,
        [property: JsonPropertyName("payloadUrl")] string PayloadUrl,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("issuedUnixMilliseconds")] long IssuedUnixMilliseconds,
        [property: JsonPropertyName("canonical")] string Canonical,
        [property: JsonPropertyName("signature")] string Signature,
        [property: JsonPropertyName("invocationUrl")] string InvocationUrl);

    private static ContractFixture Fixture() =>
        JsonSerializer.Deserialize<ContractFixture>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RoutePacer", "Fixtures", "routepacer-contract-v1.json")))
        ?? throw new InvalidOperationException("The contract fixture could not be read.");

    // Returns exactly what the fixture recorded, so the invocation URL assertion can compare the
    // whole string instead of everything except the one non-deterministic field.
    private sealed class FixedSigner(string signature) : IRoutePacerInvocationSigner
    {
        public string Sign(ReadOnlySpan<byte> canonicalBytes) => signature;
    }

    [Fact]
    public void Canonical_bytes_match_the_shared_contract_fixture()
    {
        var fixture = Fixture();

        var canonical = RoutePacerContract.CanonicalBytes(
            new Uri(fixture.PayloadUrl, UriKind.Absolute),
            fixture.Name,
            fixture.IssuedUnixMilliseconds);

        Assert.Equal(fixture.Canonical, Encoding.UTF8.GetString(canonical));
        Assert.Equal(Encoding.UTF8.GetBytes(fixture.Canonical), canonical);
        Assert.NotEqual((byte)'\n', canonical[^1]);
    }

    [Fact]
    public void Recorded_fixture_signature_verifies_against_the_fixture_key()
    {
        var fixture = Fixture();
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(fixture.PrivateKeyPem);

        var verified = ecdsa.VerifyData(
            RoutePacerContract.CanonicalBytes(
                new Uri(fixture.PayloadUrl, UriKind.Absolute),
                fixture.Name,
                fixture.IssuedUnixMilliseconds),
            Base64Url(fixture.Signature),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(verified, "The recorded interop signature must verify, or RoutePacer will reject real handoffs.");
    }

    [Fact]
    public void Invocation_url_matches_the_shared_contract_fixture()
    {
        var fixture = Fixture();

        var url = RoutePacerContract.BuildInvocationUrl(
            new Uri("https://pacetracking.tqaentry.com", UriKind.Absolute),
            new Uri(fixture.PayloadUrl, UriKind.Absolute),
            fixture.Name,
            DateTimeOffset.FromUnixTimeMilliseconds(fixture.IssuedUnixMilliseconds),
            new FixedSigner(fixture.Signature));

        Assert.Equal(fixture.InvocationUrl, url.AbsoluteUri);
    }

    [Fact]
    public void Live_signature_over_the_fixture_canonical_bytes_verifies()
    {
        var fixture = Fixture();
        using var signer = EcdsaRoutePacerInvocationSigner.FromPem(fixture.PrivateKeyPem);
        var canonical = RoutePacerContract.CanonicalBytes(
            new Uri(fixture.PayloadUrl, UriKind.Absolute),
            fixture.Name,
            fixture.IssuedUnixMilliseconds);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(fixture.PrivateKeyPem);

        Assert.True(ecdsa.VerifyData(
            canonical,
            Base64Url(signer.Sign(canonical)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Café & coast / return")]
    public void Name_is_signed_unescaped_and_query_encoded_once(string? name)
    {
        var fixture = Fixture();
        var payload = new Uri(fixture.PayloadUrl, UriKind.Absolute);
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(fixture.IssuedUnixMilliseconds);

        var canonical = Encoding.UTF8.GetString(
            RoutePacerContract.CanonicalBytes(payload, name, fixture.IssuedUnixMilliseconds));
        var url = RoutePacerContract.BuildInvocationUrl(
            new Uri("https://pacetracking.tqaentry.com", UriKind.Absolute),
            payload,
            name,
            issuedAt,
            new FixedSigner("sig"));

        Assert.Equal($"rt\n1\n{fixture.PayloadUrl}\n{name ?? string.Empty}\n{fixture.IssuedUnixMilliseconds}", canonical);
        Assert.Contains($"&name={Uri.EscapeDataString(name ?? string.Empty)}&", url.OriginalString, StringComparison.Ordinal);
        // A single encoding pass: a doubly encoded name would show %2526 for the ampersand.
        Assert.DoesNotContain("%25", url.OriginalString, StringComparison.Ordinal);
        // Query encoding, not form encoding: a form encoder turns the space into '+', which
        // RoutePacer would then verify against a name that never contained one.
        Assert.DoesNotContain("+", url.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public void Invocation_url_emits_each_contract_key_once_in_order()
    {
        var fixture = Fixture();

        var url = RoutePacerContract.BuildInvocationUrl(
            new Uri("https://pacetracking.tqaentry.com", UriKind.Absolute),
            new Uri(fixture.PayloadUrl, UriKind.Absolute),
            fixture.Name,
            DateTimeOffset.FromUnixTimeMilliseconds(fixture.IssuedUnixMilliseconds),
            new FixedSigner(fixture.Signature));

        var keys = url.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=')[0]).ToArray();
        Assert.Equal(["src", "v", "payload", "name", "ts", "sig"], keys);
        Assert.Equal("/open", url.AbsolutePath);
    }

    [Fact]
    public void Signer_returns_64_byte_P1363_signature_as_base64url()
    {
        var fixture = Fixture();
        using var signer = EcdsaRoutePacerInvocationSigner.FromPem(fixture.PrivateKeyPem);

        var signature = signer.Sign(Encoding.UTF8.GetBytes(fixture.Canonical));

        Assert.DoesNotContain('=', signature);
        Assert.DoesNotContain('+', signature);
        Assert.DoesNotContain('/', signature);
        Assert.Equal(64, Base64Url(signature).Length);
    }

    [Fact]
    public void Signer_rejects_a_non_P256_private_key()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var pem = p384.ExportPkcs8PrivateKeyPem();

        Assert.Throws<InvalidOperationException>(() => EcdsaRoutePacerInvocationSigner.FromPem(pem));
    }

    [Fact]
    public void Disabled_signer_refuses_to_sign()
    {
        var signer = new DisabledRoutePacerInvocationSigner();

        Assert.Throws<InvalidOperationException>(() => signer.Sign("rt"u8));
    }

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
