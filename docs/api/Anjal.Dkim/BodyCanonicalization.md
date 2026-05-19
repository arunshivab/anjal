# BodyCanonicalization

**Namespace:** `Anjal.Dkim`

RFC 6376 body canonicalization algorithm.

## Members

- **Relaxed** *(field)* - Trailing whitespace stripped from each line; runs of internal whitespace collapsed to a single SP; trailing empty lines removed. The default.
- **Simple** *(field)* - Body is reproduced verbatim except trailing empty lines.
