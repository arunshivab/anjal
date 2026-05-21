# SmtpUserResponse

**Namespace:** `Anjal.Api.Dto`

Response body for an SMTP user. Critically, the password hash is never returned by the API - only metadata.

## Members

- **AllowedFromDomains** *(property)* - Allowed-from domains.
- **Enabled** *(property)* - Whether the user is enabled.
- **Id** *(property)* - Identifier assigned by the store.
- **UpdatedAt** *(property)* - When the user was created or last updated.
- **Username** *(property)* - The username.
