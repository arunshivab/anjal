# TlsCipherSet

**Namespace:** `Anjal.Smtp`

The TLS cipher suites Anjal offers when it connects to another mail server, most preferred first, and helpers to describe a negotiated session and a failed one.

## Members

- **Describe** *(method)* - A short description of a negotiated session, such as TLSv1.3 TLS_AES_256_GCM_SHA384.
- **ErrorChain** *(method)* - Every message in an exception chain, outermost first, so a log line shows the real cause instead of "see inner exception".
- **OutboundPolicy** *(method)* - The policy to use for an outbound handshake: the suites above on Linux, and null (platform defaults) elsewhere.
- **OutboundSuites** *(property)* - The suites offered on outbound connections, most preferred first.
