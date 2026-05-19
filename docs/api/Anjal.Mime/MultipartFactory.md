# MultipartFactory

**Namespace:** `Anjal.Mime`

Helpers for constructing entities with valid boundary parameters. Boundaries are generated from cryptographically strong random bytes to avoid collision with the content.

## Members

- **Create** *(method)* - Create a multipart entity with the given subtype (e.g. "mixed", "alternative", "related") and an auto-generated boundary string.
- **GenerateBoundary** *(method)* - Generate a 32-character hex boundary token. Hex digits are guaranteed not to appear within base64 or quoted-printable content lines so a matching false boundary in body content is vanishingly unlikely.
