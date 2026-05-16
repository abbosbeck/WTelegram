using System.Security.Cryptography;
using Application.Configuration;
using Application.Security;
using Microsoft.Extensions.Options;

namespace Infrastructure.Security;

public sealed class AesGcmSessionCipher : ISessionCipher, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly AesGcm _aes;

    public AesGcmSessionCipher(IOptions<SessionOptions> options)
    {
        var raw = options.Value.EncryptionKey;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Sessions:EncryptionKey is not configured.");

        byte[] key;
        try { key = Convert.FromBase64String(raw); }
        catch (FormatException) { throw new InvalidOperationException("Sessions:EncryptionKey must be valid base64."); }

        if (key.Length != KeySize)
            throw new InvalidOperationException(
                $"Sessions:EncryptionKey must decode to {KeySize} bytes (got {key.Length}). " +
                "Regenerate with 'dotnet run -- gen-key'.");

        _aes = new AesGcm(key, TagSize);
    }

    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        _aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return (ciphertext, nonce, tag);
    }

    public byte[] Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        _aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static string GenerateKeyBase64() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySize));

    public void Dispose() => _aes.Dispose();
}
