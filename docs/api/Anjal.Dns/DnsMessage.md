# DnsMessage

**Namespace:** `Anjal.Dns`

Parsed DNS response message. Only the fields we need for MX lookups are exposed.

## Members

- **Parse** *(method)* - Parse raw response bytes. Throws on malformed input.
- **Answers** *(property)* - Answers from the answer section.
- **ResponseCode** *(property)* - The RCODE (0=NoError, 3=NXDOMAIN, 2=SERVFAIL, etc.).
- **TransactionId** *(property)* - The 16-bit transaction ID echoed by the server.
- **Truncated** *(property)* - Whether the response was truncated (TC flag set).
