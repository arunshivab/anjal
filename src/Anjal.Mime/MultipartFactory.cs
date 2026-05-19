using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Anjal.Mime;

/// <summary>
/// Helpers for constructing <see cref="MimeMultipart"/> entities with valid
/// boundary parameters. Boundaries are generated from cryptographically
/// strong random bytes to avoid collision with the content.
/// </summary>
public static class MultipartFactory
{
    /// <summary>
    /// Create a multipart entity with the given subtype (e.g. "mixed",
    /// "alternative", "related") and an auto-generated boundary string.
    /// </summary>
    /// <param name="subType">The multipart subtype.</param>
    /// <returns>A new <see cref="MimeMultipart"/> with Content-Type already set.</returns>
    public static MimeMultipart Create(string subType)
    {
        ArgumentNullException.ThrowIfNull(subType);

        string boundary = GenerateBoundary();
        var multi = new MimeMultipart();
        var ct = new ContentType("multipart", subType, new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["boundary"] = boundary,
        });
        multi.Headers.Set("Content-Type", ct.ToHeaderValue());
        return multi;
    }

    /// <summary>
    /// Generate a 32-character hex boundary token. Hex digits are guaranteed
    /// not to appear within base64 or quoted-printable content lines so a
    /// matching false boundary in body content is vanishingly unlikely.
    /// </summary>
    /// <returns>A boundary string.</returns>
    public static string GenerateBoundary()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder("=_Anjal_", 32);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
