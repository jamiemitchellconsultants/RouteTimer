using System.Security.Cryptography;
using System.Text;

namespace RouteTimer.Services.Security;

public sealed record ProtectedSecret(int Version, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public interface ISecretProtector
{
    ProtectedSecret Protect(string plaintext);

    string Unprotect(ProtectedSecret protectedSecret);
}

/// <summary>
/// AES-GCM protection for a single class of secret. The purpose string becomes the additional
/// authenticated data, so a ciphertext written for one purpose cannot be decrypted as another even
/// under the same key.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector, IDisposable
{
    public const int EncryptionVersion = 1;
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[] key;
    private readonly byte[] additionalData;
    private bool disposed;

    public AesGcmSecretProtector(byte[] key, string purpose)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (key.Length != KeyLength)
        {
            throw new ArgumentException("The secret protection key must be 32 bytes.", nameof(key));
        }

        this.key = key.ToArray();
        additionalData = Encoding.UTF8.GetBytes(purpose);
    }

    public ProtectedSecret Protect(string plaintext)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[bytes.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, bytes, ciphertext, tag, additionalData);
            return new ProtectedSecret(EncryptionVersion, nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string Unprotect(ProtectedSecret protectedSecret)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(protectedSecret);
        Validate(protectedSecret);

        var plaintext = new byte[protectedSecret.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(
                protectedSecret.Nonce,
                protectedSecret.Ciphertext,
                protectedSecret.Tag,
                plaintext,
                additionalData);
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

    private static void Validate(ProtectedSecret protectedSecret)
    {
        if (protectedSecret.Version != EncryptionVersion)
        {
            throw new ArgumentException("Unsupported secret encryption version.", nameof(protectedSecret));
        }

        if (protectedSecret.Nonce is null || protectedSecret.Nonce.Length != NonceLength ||
            protectedSecret.Ciphertext is null || protectedSecret.Ciphertext.Length == 0 ||
            protectedSecret.Tag is null || protectedSecret.Tag.Length != TagLength)
        {
            throw new ArgumentException("The protected secret has an invalid AES-GCM shape.", nameof(protectedSecret));
        }
    }
}
