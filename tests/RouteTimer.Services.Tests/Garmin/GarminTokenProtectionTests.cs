using System.Security.Cryptography;
using System.Text;
using RouteTimer.Services.Garmin;

namespace RouteTimer.Services.Tests.Garmin;

public sealed class GarminTokenProtectionTests
{
    [Fact]
    public void Protect_round_trips_and_detects_ciphertext_tampering()
    {
        using var protector = CreateProtector();
        var protectedToken = protector.Protect("{\"di_token\":\"secret\"}");

        Assert.Equal("{\"di_token\":\"secret\"}", protector.Unprotect(protectedToken));
        Assert.DoesNotContain("secret", Convert.ToBase64String(protectedToken.Ciphertext), StringComparison.Ordinal);

        protectedToken.Ciphertext[0] ^= 0x01;
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(protectedToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void Constructor_rejects_non_32_byte_keys(int length) =>
        Assert.Throws<ArgumentException>(() => new AesGcmGarminTokenProtector(new byte[length]));

    [Fact]
    public void Protect_uses_version_one_and_fresh_fixed_size_aes_gcm_fields()
    {
        using var protector = CreateProtector();

        var first = protector.Protect("{\"token\":\"same\"}");
        var second = protector.Protect("{\"token\":\"same\"}");

        Assert.Equal(1, first.Version);
        Assert.Equal(12, first.Nonce.Length);
        Assert.Equal(16, first.Tag.Length);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Theory]
    [InlineData("RouteTimer:OtherPurpose:1:1")]
    [InlineData("RouteTimer:GarminToken:2:1")]
    [InlineData("RouteTimer:GarminToken:1:2")]
    public void Protect_authenticates_purpose_connection_row_and_version_as_additional_data(string wrongAdditionalData)
    {
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        using var protector = new AesGcmGarminTokenProtector(key);
        var protectedToken = protector.Protect("{\"token\":\"secret\"}");
        var plaintext = new byte[protectedToken.Ciphertext.Length];

        using var aes = new AesGcm(key, protectedToken.Tag.Length);
        aes.Decrypt(
            protectedToken.Nonce,
            protectedToken.Ciphertext,
            protectedToken.Tag,
            plaintext,
            "RouteTimer:GarminToken:1:1"u8);

        Assert.Equal("{\"token\":\"secret\"}", Encoding.UTF8.GetString(plaintext));
        Assert.Throws<AuthenticationTagMismatchException>(() => aes.Decrypt(
            protectedToken.Nonce,
            protectedToken.Ciphertext,
            protectedToken.Tag,
            plaintext,
            Encoding.UTF8.GetBytes(wrongAdditionalData)));
    }

    [Fact]
    public void Protect_rejects_empty_token_json()
    {
        using var protector = CreateProtector();

        Assert.Throws<ArgumentException>(() => protector.Protect(string.Empty));
    }

    [Fact]
    public void Unprotect_rejects_unsupported_versions()
    {
        using var protector = CreateProtector();
        var protectedToken = protector.Protect("{\"token\":\"secret\"}");

        var exception = Assert.Throws<ArgumentException>(() => protector.Unprotect(protectedToken with { Version = 2 }));

        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(11, 1, 16)]
    [InlineData(13, 1, 16)]
    [InlineData(12, 0, 16)]
    [InlineData(12, 1, 15)]
    [InlineData(12, 1, 17)]
    public void Unprotect_rejects_invalid_protected_token_shapes(int nonceLength, int ciphertextLength, int tagLength)
    {
        using var protector = CreateProtector();
        var protectedToken = new ProtectedGarminToken(
            1,
            new byte[nonceLength],
            new byte[ciphertextLength],
            new byte[tagLength]);

        Assert.Throws<ArgumentException>(() => protector.Unprotect(protectedToken));
    }

    private static AesGcmGarminTokenProtector CreateProtector() =>
        new(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
}
