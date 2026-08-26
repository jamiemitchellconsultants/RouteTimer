using System.Security.Cryptography;
using RouteTimer.Services.Garmin;
using RouteTimer.Services.Security;

namespace RouteTimer.Services.Tests.Security;

public sealed class AesGcmSecretProtectorTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trips_a_secret()
    {
        using var protector = new AesGcmSecretProtector(Key(), "RouteTimer:Test:1:1");

        var protectedSecret = protector.Protect("a-secret-value");

        Assert.Equal("a-secret-value", protector.Unprotect(protectedSecret));
    }

    [Fact]
    public void A_ciphertext_written_for_one_purpose_does_not_decrypt_under_another()
    {
        var key = Key();
        using var writer = new AesGcmSecretProtector(key, "RouteTimer:PurposeA:1:1");
        using var reader = new AesGcmSecretProtector(key, "RouteTimer:PurposeB:1:1");

        var protectedSecret = writer.Protect("a-secret-value");

        Assert.Throws<AuthenticationTagMismatchException>(() => reader.Unprotect(protectedSecret));
    }

    [Fact]
    public void The_garmin_protector_still_reads_its_existing_additional_data()
    {
        var key = Key();
        using var garmin = new AesGcmGarminTokenProtector(key);
        using var equivalent = new AesGcmSecretProtector(key, "RouteTimer:GarminToken:1:1");

        var token = garmin.Protect("{\"token\":\"value\"}");
        var asSecret = new ProtectedSecret(token.Version, token.Nonce, token.Ciphertext, token.Tag);

        Assert.Equal("{\"token\":\"value\"}", equivalent.Unprotect(asSecret));
    }

    [Fact]
    public void Rejects_a_key_that_is_not_thirty_two_bytes()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16], "RouteTimer:Test:1:1"));
    }
}
