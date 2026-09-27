# TlsClientOptions

**Namespace:** `Anjal.Smtp`

TLS configuration for an outbound sender. Determines whether to attempt STARTTLS and how strict to be when the remote server lacks it.

## Members

- **ResolveModeAsync** *(method)* - Resolve the effective TLS mode for a destination, applying the policy lookup if available and falling back to the default.
- **AllowPlaintext** *(property)* - Whether a message may be sent unencrypted when encryption cannot be set up (the server does not offer STARTTLS, or the destination's policy is Disabled). Default true for library compatibility; the Anjal server sets it false unless ANJAL_TLS_ALLOW_PLAINTEXT=true, so it never sends mail unencrypted: such a message is held, retried, and finally returned to its sender. A relay on the loopback interface is exempt - nothing leaves the machine.
- **DefaultMode** *(property)* - The TLS mode. The default is opportunistic which is what every legitimate MTA does: try STARTTLS, fall back to plaintext if the server does not advertise it. Override per-destination via the store's outbound_tls_policies table.
- **PolicyLookup** *(property)* - A function that returns the TLS mode for a given destination (domain for direct, hostname for relay). May return null to use . Typically backed by the store.
- **Revocation** *(property)* - Revocation checking when a certificate is validated (Required mode and relays). Default .
- **ValidateCertificate** *(property)* - When true (default), the server's certificate is validated against the system trust store. Set false ONLY for testing against self-signed certificates.
