# TlsCipherSet

**Namespace:** `Anjal.Smtp`

The TLS cipher suites Anjal offers when it connects to another mail server, most preferred first, and helpers to describe a negotiated session and a failed one.

## Members

- **Describe** *(method)* - A short description of a negotiated session, such as TLSv1.3 TLS_AES_256_GCM_SHA384.
- **ErrorChain** *(method)* - Every message in an exception chain, outermost first, so a log line shows the real cause instead of "see inner exception".
- **OutboundPolicy** *(method)* - The policy to use for an outbound handshake: the suites above on Linux, and null (platform defaults) elsewhere.
- **WebmailPolicy** *(method)* - The policy for the webmail's HTTPS handshakes: on Linux, and null (platform defaults) elsewhere.
- **OutboundSuites** *(property)* - The suites offered on outbound connections, most preferred first.
- **WebmailSuites** *(property)* - The suites the webmail offers browsers, most preferred first: TLS 1.3, then TLS 1.2 with ECDHE and AES-GCM or ChaCha20 (no CBC, no DHE, no static RSA).
