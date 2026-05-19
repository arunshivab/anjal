# QuotedPrintableCodec

**Namespace:** `Anjal.Mime`

Quoted-Printable encoder and decoder per RFC 2045 section 6.7. Designed for content that is mostly US-ASCII text with occasional non-ASCII octets that need escaping.

## Members

- **Decode** *(method)* - Decode a quoted-printable string back to its original bytes. Soft line breaks ("=\r\n" or "=\n") are removed, "=XX" hex escapes are converted to their byte value, and other characters pass through unchanged.
- **Encode** *(method)* - Encode bytes as quoted-printable text. Lines are wrapped to 76 characters using soft line breaks ("=\r\n") per RFC 2045 section 6.7 rule 5. Existing CRLF in the input is preserved as hard line breaks.
