using RouteTimer.Services.Security;

namespace RouteTimer.Services.Garmin;

public sealed record ProtectedGarminToken(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public interface IGarminTokenProtector
{
    ProtectedGarminToken Protect(string tokenJson);

    string Unprotect(ProtectedGarminToken protectedToken);
}

public sealed class AesGcmGarminTokenProtector : IGarminTokenProtector, IDisposable
{
    // Load-bearing and frozen. Every Garmin token already in the database was sealed with this
    // exact additional authenticated data; changing a single byte makes all of them undecryptable.
    private const string Purpose = "RouteTimer:GarminToken:1:1";
    private readonly AesGcmSecretProtector inner;

    public AesGcmGarminTokenProtector(byte[] key) => inner = new AesGcmSecretProtector(key, Purpose);

    public ProtectedGarminToken Protect(string tokenJson)
    {
        var secret = inner.Protect(tokenJson);
        return new ProtectedGarminToken(secret.Version, secret.Nonce, secret.Ciphertext, secret.Tag);
    }

    public string Unprotect(ProtectedGarminToken protectedToken)
    {
        ArgumentNullException.ThrowIfNull(protectedToken);
        return inner.Unprotect(new ProtectedSecret(
            protectedToken.Version,
            protectedToken.Nonce,
            protectedToken.Ciphertext,
            protectedToken.Tag));
    }

    public void Dispose() => inner.Dispose();
}
