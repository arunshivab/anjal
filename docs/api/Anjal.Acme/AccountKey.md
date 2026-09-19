# AccountKey

**Namespace:** `Anjal.Acme`

The ACME account key: ECDSA P-256, used to sign every request as a JWS with the ES256 algorithm. Exposed as a JWK for account creation and as a thumbprint for HTTP-01 key authorizations (RFC 7638).

## Members

- **Create** *(method)* - Generate a fresh P-256 key.
- **Dispose** *(method)* - _(no description)_
- **FromJwk** *(method)* - Build a verifier from a JWK JSON object (crv/kty/x/y).
- **FromPem** *(method)* - Load from a PKCS#8 or EC PEM.
- **Sign** *(method)* - ES256 signature (raw R||S, 64 bytes) over the signing input.
- **ToPem** *(method)* - Export as PKCS#8 PEM.
- **Verify** *(method)* - Verify an ES256 signature produced by . Used by tests and the fake CA.
- **JwkJson** *(property)* - The public key as a JWK object with members in the lexicographic order RFC 7638 requires for thumbprinting: crv, kty, x, y.
- **Thumbprint** *(property)* - RFC 7638 thumbprint: base64url(SHA-256(canonical JWK)).
