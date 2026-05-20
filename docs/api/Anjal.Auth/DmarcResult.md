# DmarcResult

**Namespace:** `Anjal.Auth`

DMARC evaluation result per RFC 7489 section 6.6.

## Members

- **Fail** *(field)* - Both SPF and DKIM either failed or weren't aligned.
- **None** *(field)* - No DMARC record exists for the domain, or no check has been performed. This is the default for an uninitialized result.
- **Pass** *(field)* - At least one of SPF or DKIM passed and was aligned.
- **PermError** *(field)* - DMARC record was malformed.
- **TempError** *(field)* - DNS lookup failure or transient processing error.
