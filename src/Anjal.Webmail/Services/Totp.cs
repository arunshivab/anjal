using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Anjal.Webmail.Services;

/// <summary>
/// Time-based one-time codes (RFC 6238, SHA-1, six digits, thirty-second
/// steps): what every authenticator app shows. A code is accepted for the
/// step before and after the current one, so a phone a little out of time
/// still works, and never twice.
/// </summary>
public static class Totp
{
    /// <summary>Digits in a code.</summary>
    public const int Digits = 6;

    /// <summary>Seconds a code lasts.</summary>
    public const int StepSeconds = 30;

    /// <summary>Bytes in a new secret: 160 bits, as RFC 4226 recommends.</summary>
    public const int SecretBytes = 20;

    /// <summary>A new random secret.</summary>
    /// <returns>The secret.</returns>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The step a moment falls in.</summary>
    /// <param name="at">The moment.</param>
    /// <returns>Seconds since 1970 divided by thirty.</returns>
    public static long StepOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for a step.</summary>
    /// <param name="secret">The secret.</param>
    /// <param name="step">The step.</param>
    /// <returns>Six digits, with leading zeros.</returns>
    public static string Code(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        // RFC 6238 as every authenticator app implements it is HMAC-SHA-1; as an
        // HMAC over a random 160-bit key it is not weakened by SHA-1's collisions.
#pragma warning disable CA5350
        byte[] hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Check a code typed now. Returns the step it matched, or null; a step at
    /// or before <paramref name="lastUsedStep"/> is refused, so a code seen
    /// over someone's shoulder cannot be used again.
    /// </summary>
    /// <param name="secret">The secret.</param>
    /// <param name="code">What was typed; spaces are ignored.</param>
    /// <param name="now">Now.</param>
    /// <param name="lastUsedStep">The step of the last accepted code.</param>
    /// <returns>The matched step, or null.</returns>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        ArgumentNullException.ThrowIfNull(secret);
        string typed = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (typed.Length != Digits)
        {
            return null;
        }
        long current = StepOf(now);
        long? matched = null;
        for (long step = current - 1; step <= current + 1; step++)
        {
            // Every candidate is compared, in constant time, so timing tells nothing.
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(typed)) && step > lastUsedStep)
            {
                matched ??= step;
            }
        }
        return matched;
    }

    /// <summary>The link an authenticator app reads from the picture.</summary>
    /// <param name="issuer">Who the code is for, for example "Imagiqa mail".</param>
    /// <param name="account">The address.</param>
    /// <param name="secret">The secret.</param>
    /// <returns>An otpauth:// address.</returns>
    public static string Uri(string issuer, string account, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(secret);
        string label = System.Uri.EscapeDataString(issuer) + ":" + System.Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";
    }
}

/// <summary>Base32 (RFC 4648), without padding: how authenticator secrets are written.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Encode bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>Upper-case letters and digits 2-7.</returns>
    public static string Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }
        return sb.ToString();
    }

    /// <summary>Decode text; spaces, dashes and case are ignored. Null when it is not Base32.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes, or null.</returns>
    public static byte[]? Decode(string? text)
    {
        var bytes = new List<byte>();
        int buffer = 0;
        int bits = 0;
        foreach (char raw in text ?? string.Empty)
        {
            if (raw == ' ' || raw == '-' || raw == '=')
            {
                continue;
            }
            int v = Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }

    /// <summary>A secret written in groups of four, for typing into an app by hand.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>For example "JBSW Y3DP EHPK 3PXP".</returns>
    public static string Grouped(byte[] secret)
    {
        string s = Encode(secret);
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i += 4)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(s.AsSpan(i, Math.Min(4, s.Length - i)));
        }
        return sb.ToString();
    }
}
