# Base64Codec

**Namespace:** `Anjal.Mime`

Hand-rolled Base64 encoder and decoder per RFC 4648. Used for Content- Transfer-Encoding base64 (RFC 2045 section 6.8). Tolerates whitespace and line breaks in input as required by RFC 2045.

## Members

- **Decode** *(method)* - Decode a Base64-encoded string. Whitespace, CR, LF, and tab characters are silently skipped. Characters outside the Base64 alphabet (other than '=' padding and whitespace) throw .
- **Encode** *(method)* - Encode a byte array as Base64, breaking the output into lines of 76 characters separated by CRLF per RFC 2045 section 6.8.
