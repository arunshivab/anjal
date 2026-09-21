using System.Security.Cryptography;
using System.Text;

namespace Anjal.Store;

/// <summary>
/// Envelope encryption for secrets held in the database - DKIM private keys
/// today. Values are sealed with AES-256-GCM under a key-encryption key
/// (KEK) that lives only in the server's environment file, never in the
/// database. A stolen database dump, or a backup read without the KEK,
/// yields ciphertext.
/// <para>
/// Sealed values are stored as <c>enc:v1:</c> followed by base64 of
/// nonce (12 bytes), ciphertext and tag (16 bytes). A stored value without
/// that prefix is treated as plaintext, so keys written before encryption
/// was configured keep working and are re-sealed the next time they are
/// saved.
/// </para>
/// </summary>
public sealed class SecretProtector
{
    /// <summary>Prefix marking a sealed value.</summary>
    public const string Prefix = "enc:v1:";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private readonly byte[] key;

    /// <summary>Construct from a 32-byte key.</summary>
    /// <param name="key">The KEK.</param>
    public SecretProtector(byte[] key)
    {
        System.ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new System.ArgumentException("The key-encryption key must be exactly 32 bytes (256 bits).", nameof(key));
        }
        this.key = (byte[])key.Clone();
    }

    /// <summary>
    /// The protector configured by <c>ANJAL_KEK</c> (base64 of 32 random
    /// bytes, e.g. <c>openssl rand -base64 32</c>), or null when unset.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">ANJAL_KEK is set but is not 32 bytes of base64.</exception>
    public static SecretProtector? FromEnvironment()
    {
        string? value = System.Environment.GetEnvironmentVariable("ANJAL_KEK");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        byte[] bytes;
        try
        {
            bytes = System.Convert.FromBase64String(value.Trim());
        }
        catch (System.FormatException ex)
        {
            throw new System.InvalidOperationException("ANJAL_KEK must be base64 (generate with: openssl rand -base64 32).", ex);
        }
        if (bytes.Length != 32)
        {
            throw new System.InvalidOperationException($"ANJAL_KEK decodes to {bytes.Length} bytes; it must be 32.");
        }
        return new SecretProtector(bytes);
    }

    /// <summary>Whether a stored value is sealed.</summary>
    /// <param name="stored">The stored value.</param>
    public static bool IsSealed(string stored)
    {
        System.ArgumentNullException.ThrowIfNull(stored);
        return stored.StartsWith(Prefix, System.StringComparison.Ordinal);
    }

    /// <summary>Seal a plaintext secret.</summary>
    /// <param name="plaintext">The secret.</param>
    /// <param name="associatedData">Context bound into the tag (e.g. the domain and selector), so a sealed value cannot be moved to another row.</param>
    public string Seal(string plaintext, string associatedData)
    {
        System.ArgumentNullException.ThrowIfNull(plaintext);
        System.ArgumentNullException.ThrowIfNull(associatedData);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] clear = Encoding.UTF8.GetBytes(plaintext);
        byte[] cipher = new byte[clear.Length];
        byte[] tag = new byte[TagBytes];
        using (var aes = new AesGcm(this.key, TagBytes))
        {
            aes.Encrypt(nonce, clear, cipher, tag, Encoding.UTF8.GetBytes(associatedData));
        }
        CryptographicOperations.ZeroMemory(clear);
        byte[] packed = new byte[NonceBytes + cipher.Length + TagBytes];
        System.Buffer.BlockCopy(nonce, 0, packed, 0, NonceBytes);
        System.Buffer.BlockCopy(cipher, 0, packed, NonceBytes, cipher.Length);
        System.Buffer.BlockCopy(tag, 0, packed, NonceBytes + cipher.Length, TagBytes);
        return Prefix + System.Convert.ToBase64String(packed);
    }

    /// <summary>Open a sealed value; plaintext values are returned unchanged.</summary>
    /// <param name="stored">The stored value.</param>
    /// <param name="associatedData">The same context used when sealing.</param>
    /// <exception cref="CryptographicException">The value was sealed with a different key or has been altered.</exception>
    public string Open(string stored, string associatedData)
    {
        System.ArgumentNullException.ThrowIfNull(stored);
        System.ArgumentNullException.ThrowIfNull(associatedData);
        if (!IsSealed(stored))
        {
            return stored;
        }
        byte[] packed = System.Convert.FromBase64String(stored.Substring(Prefix.Length));
        if (packed.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Sealed value is truncated.");
        }
        int cipherLength = packed.Length - NonceBytes - TagBytes;
        byte[] clear = new byte[cipherLength];
        using (var aes = new AesGcm(this.key, TagBytes))
        {
            aes.Decrypt(
                packed.AsSpan(0, NonceBytes),
                packed.AsSpan(NonceBytes, cipherLength),
                packed.AsSpan(NonceBytes + cipherLength, TagBytes),
                clear,
                Encoding.UTF8.GetBytes(associatedData));
        }
        string text = Encoding.UTF8.GetString(clear);
        CryptographicOperations.ZeroMemory(clear);
        return text;
    }
}
