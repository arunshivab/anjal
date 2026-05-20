# AuthenticationResultsBuilder

**Namespace:** `Anjal.Auth`

Formats an RFC 8601 Authentication-Results header value from SPF, DKIM, and DMARC verdicts. The format Anjal produces is interoperable with what Postfix, OpenDKIM, OpenDMARC, and mail processors generally expect.

## Members

- **Build** *(method)* - Build the header VALUE (no field name prefix, no terminating CRLF). The caller prepends Authentication-Results: and the CRLF.
