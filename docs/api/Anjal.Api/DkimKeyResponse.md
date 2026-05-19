# DkimKeyResponse

**Namespace:** `Anjal.Api.Dto`

Response body for DKIM key operations. The private key is NEVER returned in API responses - only the metadata.

## Members

- **Domain** *(property)* - The sender domain.
- **Id** *(property)* - Identifier assigned by the store.
- **Selector** *(property)* - The selector.
- **UpdatedAt** *(property)* - When the key was created or last updated.
