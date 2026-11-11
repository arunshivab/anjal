using System.Security.Cryptography;
using System.Text;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.28 (DES-19 §7): the secure link's keys. The token (32 random bytes, in the link only) gives, by
/// HKDF-SHA256, one key for the sealed mail and one for the codes; Anjal keeps only the token's SHA-256,
/// so a copy of the database cannot open a secure mail or check a code.
/// </summary>
public static class SecureLinkCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] SealInfo = Encoding.ASCII.GetBytes("Anjal.SecureLink.v1");
    private static readonly byte[] CodeInfo = Encoding.ASCII.GetBytes("Anjal.SecureLink.Code.v1");
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ASL1");

    /// <summary>A new token, as it goes in the link (base64url, 43 characters).</summary>
    /// <returns>The token.</returns>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The token's SHA-256 (hex), which is all Anjal keeps of it.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The hash, or empty for something that is not a token.</returns>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Bytes(token) is byte[] raw ? Convert.ToHexStringLower(SHA256.HashData(raw)) : string.Empty;
    }

    /// <summary>Seal the mail under the token's key.</summary>
    /// <param name="token">The token.</param>
    /// <param name="mail">The mail.</param>
    /// <returns>The sealed bytes.</returns>
    public static byte[] Seal(string token, byte[] mail)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(mail);
        byte[] key = Key(token, SealInfo);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] output = new byte[Magic.Length + NonceSize + TagSize + mail.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, mail, output.AsSpan(Magic.Length + NonceSize + TagSize), output.AsSpan(Magic.Length + NonceSize, TagSize), Magic);
        CryptographicOperations.ZeroMemory(key);
        return output;
    }

    /// <summary>Open a sealed mail with the token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="sealedMail">The sealed bytes.</param>
    /// <returns>The mail, or null when the token is not the one it was sealed with.</returns>
    public static byte[]? Open(string token, byte[] sealedMail)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(sealedMail);
        if (sealedMail.Length < Magic.Length + NonceSize + TagSize || !sealedMail.AsSpan(0, Magic.Length).SequenceEqual(Magic) || Bytes(token) is null)
        {
            return null;
        }
        byte[] key = Key(token, SealInfo);
        byte[] plain = new byte[sealedMail.Length - Magic.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(sealedMail.AsSpan(Magic.Length, NonceSize), sealedMail.AsSpan(Magic.Length + NonceSize + TagSize), sealedMail.AsSpan(Magic.Length + NonceSize, TagSize), plain, Magic);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>A new 6-digit code.</summary>
    /// <returns>The code, six digits.</returns>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A code's keyed hash under the token's code key (hex).</summary>
    /// <param name="token">The token.</param>
    /// <param name="code">The code (spaces ignored).</param>
    /// <returns>The hash.</returns>
    public static string CodeHash(string token, string code)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(code);
        byte[] key = Key(token, CodeInfo);
        string digits = new(code.Where(char.IsAsciiDigit).ToArray());
        string hash = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(digits)));
        CryptographicOperations.ZeroMemory(key);
        return hash;
    }

    /// <summary>Two hex hashes compared in constant time.</summary>
    /// <param name="a">One.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when equal.</returns>
    public static bool Same(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    private static byte[] Key(string token, byte[] info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Bytes(token) ?? Array.Empty<byte>(), 32, salt: Array.Empty<byte>(), info: info);

    private static byte[]? Bytes(string token)
    {
        if (token.Length != 43 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }
        string padded = token.Replace('-', '+').Replace('_', '/') + "=";
        try
        {
            byte[] raw = Convert.FromBase64String(padded);
            return raw.Length == 32 ? raw : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
