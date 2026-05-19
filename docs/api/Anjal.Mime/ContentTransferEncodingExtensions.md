# ContentTransferEncodingExtensions

**Namespace:** `Anjal.Mime`

Helpers for converting between values and the wire-format tokens defined in RFC 2045 section 6.1.

## Members

- **Parse** *(method)* - Parse the value of a Content-Transfer-Encoding header. Comparison is case-insensitive per RFC 2045 section 6.1 ("These values are not case sensitive"). Unknown or empty input returns .
- **ToHeaderValue** *(method)* - Produce the canonical wire-format token for an encoding, as used in a Content-Transfer-Encoding header.
