using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anjal.Store;

/// <summary>
/// rc.26 (DES-14 §18, D-224, D-268): mail for an application's inbox, encrypted to the application's
/// own RSA key before it is written. A fresh 256-bit key per mail, AES-256-GCM over the original
/// bytes, the key wrapped with RSA-OAEP-SHA256. The stored file, and what /inbox/{id} returns, is one
/// JSON envelope; Anjal holds no key that opens it.
/// </summary>
public static class AppEnvelope
{
    /// <summary>The algorithm's name in the envelope.</summary>
    public const string Algorithm = "RSA-OAEP-256+A256GCM";

    /// <summary>The smallest RSA key accepted, in bits.</summary>
    public const int MinimumBits = 3072;

    /// <summary>
    /// Read an application's public key (PEM, "BEGIN PUBLIC KEY"); the error when it is not an RSA
    /// public key of 3,072 bits or more.
    /// </summary>
    /// <param name="pem">The PEM text.</param>
    /// <param name="fingerprint">The key's fingerprint: the hex SHA-256 of its DER form.</param>
    /// <returns>The error, or null.</returns>
    public static string? Check(string pem, out string fingerprint)
    {
        System.ArgumentNullException.ThrowIfNull(pem);
        fingerprint = string.Empty;
        if (pem.Contains("PRIVATE KEY", System.StringComparison.Ordinal))
        {
            return "That is a private key. Paste the public key only; the private key stays with the application.";
        }
        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < MinimumBits)
            {
                return $"The key has {rsa.KeySize} bits; Anjal needs {MinimumBits} or more.";
            }
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
            return null;
        }
        catch (System.ArgumentException)
        {
            return "That is not an RSA public key in PEM form (-----BEGIN PUBLIC KEY-----).";
        }
        catch (CryptographicException)
        {
            return "That is not an RSA public key in PEM form (-----BEGIN PUBLIC KEY-----).";
        }
    }

    /// <summary>The key id an envelope names: the first 16 characters of the fingerprint.</summary>
    /// <param name="fingerprint">The fingerprint.</param>
    /// <returns>The key id.</returns>
    public static string KeyIdOf(string fingerprint)
    {
        System.ArgumentNullException.ThrowIfNull(fingerprint);
        return fingerprint.Length > 16 ? fingerprint[..16] : fingerprint;
    }

    /// <summary>Encrypt a mail to an application's key.</summary>
    /// <param name="raw">The mail as received.</param>
    /// <param name="publicKeyPem">The application's public key.</param>
    /// <param name="meta">What stays readable beside it: receivedAt, size, spamScore, auth, inReplyToSend.</param>
    /// <returns>The envelope's bytes (UTF-8 JSON).</returns>
    public static byte[] Seal(byte[] raw, string publicKeyPem, IReadOnlyDictionary<string, object?> meta)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        System.ArgumentNullException.ThrowIfNull(publicKeyPem);
        System.ArgumentNullException.ThrowIfNull(meta);
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[raw.Length];
        try
        {
            using (var gcm = new AesGcm(key, 16))
            {
                gcm.Encrypt(iv, raw, cipher, tag);
            }
            byte[] wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
            var envelope = new Dictionary<string, object?>
            {
                ["v"] = 1,
                ["alg"] = Algorithm,
                ["keyId"] = KeyIdOf(fingerprint),
                ["wrappedKey"] = Convert.ToBase64String(wrapped),
                ["iv"] = Convert.ToBase64String(iv),
                ["tag"] = Convert.ToBase64String(tag),
                ["ciphertext"] = Convert.ToBase64String(cipher),
                ["meta"] = meta,
            };
            return JsonSerializer.SerializeToUtf8Bytes(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The readable part of an envelope ("meta"), as JSON; "{}" when it cannot be read.</summary>
    /// <param name="envelope">The envelope's bytes.</param>
    /// <returns>The meta JSON.</returns>
    public static string MetaOf(byte[] envelope)
    {
        System.ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(envelope);
            return doc.RootElement.TryGetProperty("meta", out JsonElement meta) ? meta.GetRawText() : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    /// <summary>Open an envelope with the application's private key (tests and tools; applications use Anjal.Client).</summary>
    /// <param name="envelope">The envelope's bytes.</param>
    /// <param name="privateKey">The private key.</param>
    /// <returns>The mail as received.</returns>
    public static byte[] Open(byte[] envelope, RSA privateKey)
    {
        System.ArgumentNullException.ThrowIfNull(envelope);
        System.ArgumentNullException.ThrowIfNull(privateKey);
        using JsonDocument doc = JsonDocument.Parse(envelope);
        JsonElement root = doc.RootElement;
        byte[] key = privateKey.Decrypt(Convert.FromBase64String(root.GetProperty("wrappedKey").GetString()!), RSAEncryptionPadding.OaepSHA256);
        try
        {
            byte[] cipher = Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!);
            byte[] plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(Convert.FromBase64String(root.GetProperty("iv").GetString()!), cipher, Convert.FromBase64String(root.GetProperty("tag").GetString()!), plain);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Whether bytes look like an application envelope.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>True when they do.</returns>
    public static bool IsEnvelope(byte[] bytes)
    {
        System.ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length > 20 && Encoding.ASCII.GetString(bytes, 0, 20).StartsWith("{\"v\":1,\"alg\":", System.StringComparison.Ordinal);
    }
}
