# DkimResult

**Namespace:** `Anjal.Auth`

DKIM verification result per RFC 6376 section 3.9.

## Members

- **Fail** *(field)* - Signature was syntactically valid but failed cryptographic verification.
- **None** *(field)* - No DKIM-Signature header was present, or no check has been performed. This is the default for an uninitialized result.
- **Pass** *(field)* - Signature successfully verified.
- **PermError** *(field)* - Signature had syntactic problems that prevented verification.
- **TempError** *(field)* - Verification was halted by a transient problem (e.g. DNS timeout).
