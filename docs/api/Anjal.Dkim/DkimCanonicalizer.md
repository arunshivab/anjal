# DkimCanonicalizer

**Namespace:** `Anjal.Dkim`

Implements RFC 6376 section 3.4 header and body canonicalization for both simple and relaxed algorithms. The output of these methods is the byte sequence that gets hashed (for the body, into bh=) or signed (the header set plus the DKIM-Signature line with empty b=).

## Members

- **CanonBody** *(method)* - Canonicalize the message body.
- **CanonHeader** *(method)* - Canonicalize a single header value (everything after the colon, not including the colon itself or the trailing CRLF).
- **CollapseWhitespace** *(method)* - Collapse runs of SP and HTAB into a single SP. Other whitespace is left.
- **TrimTrailingCrlfLines** *(method)* - Strip trailing CRLF-terminated empty lines from a body string. The non-empty content (if any) is preserved with its terminating CRLF.
- **UnfoldHeader** *(method)* - Unfold a header value: remove CRLF that is followed by WSP. Per RFC 5322 section 2.2.3, folding can occur anywhere FWS is allowed. The continuation WSP itself is preserved.
