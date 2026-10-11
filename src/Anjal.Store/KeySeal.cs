using System.Security.Cryptography;
using System.Text;

namespace Anjal.Store;

/// <summary>
/// DES-11 S6 (owner, 10 Oct 2026, "A"): a DKIM private key locked with the mail server's public
/// "seal" key, so that the webmail - which makes the keys - holds no secret at all, and only the
/// mail server, which signs, can open them. The sealed value also carries the DKIM public key in
/// the clear, so the webmail can still show each domain the DNS record to publish.
/// <para>
/// Format: <c>seal1:&lt;DKIM public key, SPKI base64&gt;:&lt;AES key wrapped with RSA-OAEP-SHA256, base64&gt;:&lt;nonce + ciphertext + tag, base64&gt;</c>.
/// The private key PEM is encrypted with AES-256-GCM under a fresh random key; the domain and
/// selector are bound in as associated data, so a sealed key cannot be moved to another domain.
/// </para>
/// </summary>
public static class KeySeal
{
    /// <summary>The prefix of a sealed value.</summary>
    public const string Prefix = "seal1:";

    /// <summary>The service record that publishes the seal's public key (and when it was made).</summary>
    public const string PublicRecordKind = "dkim-seal-public";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>True when a stored value is sealed this way.</summary>
    /// <param name="value">The stored value.</param>
    /// <returns>Whether it starts with <see cref="Prefix"/>.</returns>
    public static bool IsSealed(string? value) => value is not null && value.StartsWith(Prefix, System.StringComparison.Ordinal);

    /// <summary>Seal a DKIM private key with the mail server's public seal key.</summary>
    /// <param name="privateKeyPem">The DKIM private key, PEM.</param>
    /// <param name="sealPublicKey">The seal's public key, SubjectPublicKeyInfo DER.</param>
    /// <param name="domain">The domain the key signs for.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The sealed value.</returns>
    public static string Seal(string privateKeyPem, byte[] sealPublicKey, string domain, string selector)
    {
        System.ArgumentNullException.ThrowIfNull(privateKeyPem);
        System.ArgumentNullException.ThrowIfNull(sealPublicKey);
        System.ArgumentNullException.ThrowIfNull(domain);
        System.ArgumentNullException.ThrowIfNull(selector);
        byte[] dkimPublic;
        using (var dkim = RSA.Create())
        {
            dkim.ImportFromPem(privateKeyPem);
            dkimPublic = dkim.ExportSubjectPublicKeyInfo();
        }
        byte[] aesKey = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped;
        using (var seal = RSA.Create())
        {
            seal.ImportSubjectPublicKeyInfo(sealPublicKey, out _);
            wrapped = seal.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
        }
        byte[] plain = Encoding.UTF8.GetBytes(privateKeyPem);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagBytes];
        using (var gcm = new AesGcm(aesKey, TagBytes))
        {
            gcm.Encrypt(nonce, plain, cipher, tag, Context(domain, selector));
        }
        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(plain);
        byte[] body = new byte[NonceBytes + cipher.Length + TagBytes];
        nonce.CopyTo(body, 0);
        cipher.CopyTo(body, NonceBytes);
        tag.CopyTo(body, NonceBytes + cipher.Length);
        return Prefix + System.Convert.ToBase64String(dkimPublic) + ":" + System.Convert.ToBase64String(wrapped) + ":" + System.Convert.ToBase64String(body);
    }

    /// <summary>Open a sealed DKIM key with the mail server's private seal key.</summary>
    /// <param name="sealedValue">The sealed value.</param>
    /// <param name="sealPrivateKey">The seal's private key.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The DKIM private key, PEM.</returns>
    /// <exception cref="CryptographicException">The value was not sealed with this key, or for another domain.</exception>
    public static string Open(string sealedValue, RSA sealPrivateKey, string domain, string selector)
    {
        System.ArgumentNullException.ThrowIfNull(sealedValue);
        System.ArgumentNullException.ThrowIfNull(sealPrivateKey);
        System.ArgumentNullException.ThrowIfNull(domain);
        System.ArgumentNullException.ThrowIfNull(selector);
        string[] parts = Parts(sealedValue) ?? throw new CryptographicException("Not a sealed DKIM key.");
        byte[] aesKey = sealPrivateKey.Decrypt(System.Convert.FromBase64String(parts[1]), RSAEncryptionPadding.OaepSHA256);
        byte[] body = System.Convert.FromBase64String(parts[2]);
        if (body.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("The sealed DKIM key is damaged.");
        }
        byte[] plain = new byte[body.Length - NonceBytes - TagBytes];
        using (var gcm = new AesGcm(aesKey, TagBytes))
        {
            gcm.Decrypt(body.AsSpan(0, NonceBytes), body.AsSpan(NonceBytes, plain.Length), body.AsSpan(NonceBytes + plain.Length, TagBytes), plain, Context(domain, selector));
        }
        CryptographicOperations.ZeroMemory(aesKey);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>The DKIM public key carried by a sealed value (SubjectPublicKeyInfo DER), or null.</summary>
    /// <param name="sealedValue">The sealed value.</param>
    /// <returns>The public key.</returns>
    public static byte[]? DkimPublicKey(string? sealedValue) =>
        Parts(sealedValue) is { } parts ? System.Convert.FromBase64String(parts[0]) : null;

    /// <summary>The published record of the seal key, or null when none is published.</summary>
    /// <param name="store">The store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The record.</returns>
    public static async System.Threading.Tasks.Task<SealRecord?> ReadRecordAsync(IMessageStore store, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        string? json = await store.GetServiceRecordAsync(PublicRecordKind, ct).ConfigureAwait(false);
        try
        {
            return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<SealRecord>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string[]? Parts(string? value)
    {
        if (!IsSealed(value))
        {
            return null;
        }
        string[] parts = value![Prefix.Length..].Split(':');
        return parts.Length == 3 ? parts : null;
    }

    private static byte[] Context(string domain, string selector) => Encoding.UTF8.GetBytes("anjal-dkim:" + domain.ToLowerInvariant() + ":" + selector);
}

/// <summary>What is published about the mail server's seal key (DES-11 S6).</summary>
public sealed class SealRecord
{
    /// <summary>The public key, SubjectPublicKeyInfo DER, base64.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>When this seal key was made or first published.</summary>
    public System.DateTimeOffset Made { get; set; }

    /// <summary>Where its private half is kept on the mail server.</summary>
    public string Path { get; set; } = string.Empty;
}
