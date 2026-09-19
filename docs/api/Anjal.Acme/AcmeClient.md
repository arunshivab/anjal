# AcmeClient

**Namespace:** `Anjal.Acme`

RFC 8555 client. Every method is one protocol step; the sequences them. Nonces are taken from each response's Replay-Nonce header and a badNonce rejection is retried once with a fresh nonce.

## Members

- **#ctor** *(method)* - Construct.
- **BuildCsr** *(method)* - Build a PKCS#10 CSR for the domains with the given key. The first domain is the subject CN; all domains go in the SAN extension.
- **Dispose** *(method)* - _(no description)_
- **DownloadCertificateAsync** *(method)* - Download the issued certificate chain as PEM.
- **EnsureAccountAsync** *(method)* - Create the account for the account key, or find the existing one (the server returns the same account URL for a known key). Agrees to the terms of service.
- **FinalizeAsync** *(method)* - Submit the CSR.
- **GetAuthorizationAsync** *(method)* - Fetch an authorization (POST-as-GET).
- **GetChallengeAsync** *(method)* - Re-fetch a challenge to see whether validation completed.
- **GetDirectoryAsync** *(method)* - Fetch and cache the directory.
- **GetOrderAsync** *(method)* - Re-fetch an order (POST-as-GET).
- **KeyAuthorization** *(method)* - The key authorization for an HTTP-01 token: token.thumbprint.
- **NewOrderAsync** *(method)* - Create an order for the given DNS names.
- **RespondToChallengeAsync** *(method)* - Tell the server the challenge is ready to be validated.
- **AccountUrl** *(property)* - The account URL (kid) once the account exists.
