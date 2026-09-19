# Jws

**Namespace:** `Anjal.Acme`

Builds ACME request bodies: RFC 7515 JWS in flattened JSON serialization with the alg, nonce, url and either jwk (new account) or kid (everything else) protected-header members that RFC 8555 §6.2 requires.

## Members

- **Sign** *(method)* - Sign a request.
