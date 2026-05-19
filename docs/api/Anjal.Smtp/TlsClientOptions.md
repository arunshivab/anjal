# TlsClientOptions

**Namespace:** `Anjal.Smtp`

TLS configuration for an outbound sender. Determines whether to attempt STARTTLS and how strict to be when the remote server lacks it.

## Members

- **ResolveModeAsync** *(method)* - Resolve the effective TLS mode for a destination, applying the policy lookup if available and falling back to the default.
- **DefaultMode** *(property)* - The TLS mode. The default is opportunistic which is what every legitimate MTA does: try STARTTLS, fall back to plaintext if the server does not advertise it. Override per-destination via the store's outbound_tls_policies table.
- **PolicyLookup** *(property)* - A function that returns the TLS mode for a given destination (domain for direct, hostname for relay). May return null to use . Typically backed by the store.
- **ValidateCertificate** *(property)* - When true (default), the server's certificate is validated against the system trust store. Set false ONLY for testing against self-signed certificates.
