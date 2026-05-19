# EncodedWordDecoder

**Namespace:** `Anjal.Mime`

Decodes RFC 2047 encoded-word tokens of the form =?charset?encoding?encoded-text?= within header values. Used so non-ASCII text in headers (Subject, From, To) round-trips correctly.

## Members

- **Decode** *(method)* - Decode all encoded-word tokens within a header value. Tokens that cannot be decoded are left as-is in the output. Per RFC 2047 section 6.2, whitespace between two adjacent encoded words is removed.
