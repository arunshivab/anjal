
namespace Anjal.Mime;

/// <summary>
/// Helpers for converting between <see cref="ContentTransferEncoding"/> values
/// and the wire-format tokens defined in RFC 2045 section 6.1.
/// </summary>
public static class ContentTransferEncodingExtensions
{
    /// <summary>
    /// Parse the value of a Content-Transfer-Encoding header. Comparison is
    /// case-insensitive per RFC 2045 section 6.1 ("These values are not case sensitive").
    /// Unknown or empty input returns <see cref="ContentTransferEncoding.Unknown"/>.
    /// </summary>
    /// <param name="value">The header value, without the header name or colon.</param>
    /// <returns>The parsed encoding.</returns>
    public static ContentTransferEncoding Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ContentTransferEncoding.Unknown;
        }

        string trimmed = value.Trim();

        if (trimmed.Equals("7bit", StringComparison.OrdinalIgnoreCase))
        {
            return ContentTransferEncoding.SevenBit;
        }
        if (trimmed.Equals("8bit", StringComparison.OrdinalIgnoreCase))
        {
            return ContentTransferEncoding.EightBit;
        }
        if (trimmed.Equals("binary", StringComparison.OrdinalIgnoreCase))
        {
            return ContentTransferEncoding.Binary;
        }
        if (trimmed.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase))
        {
            return ContentTransferEncoding.QuotedPrintable;
        }
        if (trimmed.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            return ContentTransferEncoding.Base64;
        }

        return ContentTransferEncoding.Unknown;
    }

    /// <summary>
    /// Produce the canonical wire-format token for an encoding, as used in
    /// a Content-Transfer-Encoding header.
    /// </summary>
    /// <param name="encoding">The encoding to format.</param>
    /// <returns>The wire-format token, e.g. "base64". Defaults to "7bit" for
    /// <see cref="ContentTransferEncoding.Unknown"/> per RFC 2045 section 6.1.</returns>
    public static string ToHeaderValue(this ContentTransferEncoding encoding) => encoding switch
    {
        ContentTransferEncoding.SevenBit => "7bit",
        ContentTransferEncoding.EightBit => "8bit",
        ContentTransferEncoding.Binary => "binary",
        ContentTransferEncoding.QuotedPrintable => "quoted-printable",
        ContentTransferEncoding.Base64 => "base64",
        _ => "7bit",
    };
}
