namespace Anjal.Mime;

/// <summary>
/// The Content-Transfer-Encoding mechanism defined in RFC 2045 section 6.1.
/// Identifies how the bytes of a MIME entity body have been encoded to survive
/// transport over channels that may not preserve all octet values.
/// </summary>
public enum ContentTransferEncoding
{
    /// <summary>
    /// The encoding has not been specified or could not be recognised.
    /// Senders should treat as <see cref="SevenBit"/> per RFC 2045 section 6.1.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// 7-bit US-ASCII text. Lines are at most 998 octets long and contain
    /// no octets with the high bit set and no NUL bytes. RFC 2045 section 2.7.
    /// </summary>
    SevenBit = 1,

    /// <summary>
    /// 8-bit data. May contain octets with the high bit set but no NUL bytes,
    /// with lines at most 998 octets. RFC 2045 section 2.8.
    /// </summary>
    EightBit = 2,

    /// <summary>
    /// Arbitrary binary data with no constraints on octet values or line length.
    /// Cannot pass through SMTP without the BINARYMIME extension. RFC 2045 section 2.9.
    /// </summary>
    Binary = 3,

    /// <summary>
    /// Quoted-Printable encoding for mostly-ASCII text with occasional non-ASCII
    /// octets. RFC 2045 section 6.7.
    /// </summary>
    QuotedPrintable = 4,

    /// <summary>
    /// Base64 encoding for arbitrary binary data. RFC 2045 section 6.8.
    /// </summary>
    Base64 = 5,
}
