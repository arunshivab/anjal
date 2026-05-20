# SpfResult

**Namespace:** `Anjal.Auth`

SPF check result per RFC 7208 section 2.6.

## Members

- **Fail** *(field)* - Domain explicitly does not authorize the host. Receivers SHOULD reject.
- **Neutral** *(field)* - Domain makes no assertion about the host.
- **None** *(field)* - No SPF record exists for the domain, or no check has been performed. This is the default value for an uninitialized result - uninitialized means "no verdict computed", not "passed".
- **Pass** *(field)* - Domain explicitly authorizes the host.
- **PermError** *(field)* - SPF record is malformed (cached as a permanent error).
- **SoftFail** *(field)* - Domain weakly does not authorize (intended to be advisory).
- **TempError** *(field)* - DNS lookup failure or transient processing error.
