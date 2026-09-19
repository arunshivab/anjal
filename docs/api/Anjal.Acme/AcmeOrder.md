# AcmeOrder

**Namespace:** `Anjal.Acme`

An order as returned by the server (RFC 8555 §7.1.3).

## Members

- **Authorizations** *(property)* - Authorization URLs, one per identifier.
- **Certificate** *(property)* - Certificate URL once valid, else empty.
- **Finalize** *(property)* - Finalize URL.
- **Status** *(property)* - pending, ready, processing, valid or invalid.
- **Url** *(property)* - Order URL (from the Location header).
