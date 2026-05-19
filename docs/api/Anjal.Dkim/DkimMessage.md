# DkimMessage

**Namespace:** `Anjal.Dkim`

A parsed view of an RFC 5322 message split into headers and body for DKIM processing. Headers preserve their original casing and value exactly as they appear in the message (canonicalization is done by the signer at sign time).

## Members

- **GetHeaderValue** *(method)* - Look up the most recently-occurring header with the given name (case-insensitive). Returns the raw value or null if not present.
- **Parse** *(method)* - Parse a message into its DKIM view. Accepts either CRLF or bare LF line terminators on input; the canonicalizer normalizes to CRLF later.
- **Body** *(property)* - The body bytes (everything after the blank line between headers and body, including the body's terminating CRLF if any).
- **Headers** *(property)* - The header lines in the order they appeared. Each entry is the name and the raw value (no terminating CRLF; folded continuations included verbatim).
