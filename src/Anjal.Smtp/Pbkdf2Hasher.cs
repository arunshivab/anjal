using System.Security.Cryptography;
using System.Text;

namespace Anjal.Smtp;

/// <summary>
/// PBKDF2-SHA256 password hasher for SMTP submission user passwords. Uses
/// the BCL's <see cref="Rfc2898DeriveBytes"/> directly - no external
/// dependencies. Hash format is
/// <c>pbkdf2$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>.
///
/// <para>The default 100,000 iterations matches NIST SP 800-132 guidance
/// from 2024. For service-to-service credentials (which SMTP submission
/// users are) this is conservative; user-facing login systems should
/// consider higher counts.</para>
/// </summary>
public static class Pbkdf2Hasher
{
    /// <summary>Default iteration count.</summary>
    public const int DefaultIterations = 100_000;

    /// <summary>Hash output length in bytes (32 = 256 bits).</summary>
    private const int HashBytes = 32;

    /// <summary>Salt length in bytes (16 = 128 bits, well above the 8-byte minimum).</summary>
    private const int SaltBytes = 16;

    /// <summary>
    /// Hash a password. Returns a self-describing string of the form
    /// <c>pbkdf2$iterations$salt-b64$hash-b64</c>.
    /// </summary>
    /// <param name="password">Plaintext password.</param>
    /// <param name="iterations">PBKDF2 iteration count. Defaults to 100,000.</param>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        System.ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return "pbkdf2$" +
            iterations.ToString(System.Globalization.CultureInfo.InvariantCulture) + "$" +
            System.Convert.ToBase64String(salt) + "$" +
            System.Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Verify a password against a stored hash. Returns true on match,
    /// false on mismatch or any parse error. Uses a constant-time
    /// comparison to defend against timing attacks.
    /// </summary>
    /// <param name="password">Plaintext password from the user.</param>
    /// <param name="storedHash">Stored hash in <c>pbkdf2$...</c> form.</param>
    public static bool Verify(string password, string storedHash)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        System.ArgumentNullException.ThrowIfNull(storedHash);

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2")
        {
            return false;
        }
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = System.Convert.FromBase64String(parts[2]);
            expectedHash = System.Convert.FromBase64String(parts[3]);
        }
        catch (System.FormatException)
        {
            return false;
        }

        byte[] computedHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(expectedHash, computedHash);
    }
}
