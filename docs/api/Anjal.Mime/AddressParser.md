# AddressParser

**Namespace:** `Anjal.Mime`

Parses RFC 5322 address-list header values (e.g. From, To, Cc, Bcc). Handles display names (both bare and quoted), angle-addresses, comments, and RFC 2047 encoded-word display names. Pragmatic: accepts most real-world headers, rejects only obviously malformed input.

## Members

- **Parse** *(method)* - Parse a header value as a comma-separated list of mail addresses. Returns an empty list on null or empty input. Invalid entries are skipped silently.
- **TryParseOne** *(method)* - Try to parse a single mail address. Returns if the input cannot be parsed.
