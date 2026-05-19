# ContentTransferEncoding

**Namespace:** `Anjal.Mime`

The Content-Transfer-Encoding mechanism defined in RFC 2045 section 6.1. Identifies how the bytes of a MIME entity body have been encoded to survive transport over channels that may not preserve all octet values.

## Members

- **Base64** *(field)* - Base64 encoding for arbitrary binary data. RFC 2045 section 6.8.
- **Binary** *(field)* - Arbitrary binary data with no constraints on octet values or line length. Cannot pass through SMTP without the BINARYMIME extension. RFC 2045 section 2.9.
- **EightBit** *(field)* - 8-bit data. May contain octets with the high bit set but no NUL bytes, with lines at most 998 octets. RFC 2045 section 2.8.
- **QuotedPrintable** *(field)* - Quoted-Printable encoding for mostly-ASCII text with occasional non-ASCII octets. RFC 2045 section 6.7.
- **SevenBit** *(field)* - 7-bit US-ASCII text. Lines are at most 998 octets long and contain no octets with the high bit set and no NUL bytes. RFC 2045 section 2.7.
- **Unknown** *(field)* - The encoding has not been specified or could not be recognised. Senders should treat as per RFC 2045 section 6.1.
