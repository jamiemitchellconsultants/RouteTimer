using System.Security.Cryptography;
using System.Text;

namespace RouteTimer.Services.Garmin;

public sealed record ProtectedGarminToken(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public interface IGarminTokenProtector
{
    ProtectedGarminToken Protect(string tokenJson);

    string Unprotect(ProtectedGarminToken protectedToken);
}

public sealed class AesGcmGarminTokenProtector : IGarminTokenProtector, IDisposable
{
    private const int EncryptionVersion = 1;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private static readonly byte[] AdditionalData = "RouteTimer:GarminToken:1:1"u8.ToArray();
    private readonly byte[] key;
    private bool disposed;

    public AesGcmGarminTokenProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("Garmin token key must be 32 bytes.", nameof(key));
        }

        this.key = key.ToArray();
    }

    public ProtectedGarminToken Protect(string tokenJson)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(tokenJson);

        var plaintext = Encoding.UTF8.GetBytes(tokenJson);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData);
            return new ProtectedGarminToken(EncryptionVersion, nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(ProtectedGarminToken protectedToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(protectedToken);
        Validate(protectedToken);

        var plaintext = new byte[protectedToken.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(
                protectedToken.Nonce,
                protectedToken.Ciphertext,
                protectedToken.Tag,
                plaintext,
                AdditionalData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(key);
        disposed = true;
    }

    private static void Validate(ProtectedGarminToken protectedToken)
    {
        if (protectedToken.Version != EncryptionVersion)
        {
            throw new ArgumentException("Unsupported Garmin token encryption version.", nameof(protectedToken));
        }

        if (protectedToken.Nonce is null || protectedToken.Nonce.Length != NonceLength ||
            protectedToken.Ciphertext is null || protectedToken.Ciphertext.Length == 0 ||
            protectedToken.Tag is null || protectedToken.Tag.Length != TagLength)
        {
            throw new ArgumentException("Garmin protected token has an invalid AES-GCM shape.", nameof(protectedToken));
        }
    }
}
