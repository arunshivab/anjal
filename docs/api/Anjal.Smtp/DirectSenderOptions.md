# DirectSenderOptions

**Namespace:** `Anjal.Smtp`

Configuration for a .

## Members

- **ClientHostName** *(property)* - Hostname this client claims in EHLO (should match reverse DNS in production).
- **ConnectTimeout** *(property)* - Connect timeout per MX attempt.
- **HostLookup** *(property)* - Looks up whether a domain has an address, for implicit MX (v1.0.0-rc.9, RFC 5321 5.1). Null uses the system resolver; tests replace it.
- **Port** *(property)* - TCP port to connect to remote MXs. Always 25 on the public internet.
- **Tls** *(property)* - TLS configuration. When null, TLS is disabled. The policy lookup is keyed by destination domain (e.g. "gmail.com"), not MX hostname.
