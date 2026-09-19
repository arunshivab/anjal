# AcmeStatusResponse

**Namespace:** `Anjal.Api.Dto`

Response body for GET /api/acme.

## Members

- **AcmeDirectoryUrl** *(property)* - ACME directory URL in use.
- **ConsecutiveFailures** *(property)* - Consecutive failures since the last success.
- **DaysRemaining** *(property)* - Whole days until expiry (negative if expired).
- **Directory** *(property)* - Certificate directory on disk.
- **Domains** *(property)* - Domains recorded at issuance.
- **HasCertificate** *(property)* - Whether a certificate is stored and loadable.
- **IssuedAt** *(property)* - When the certificate was issued (UTC).
- **KeyType** *(property)* - Key type recorded at issuance.
- **LastAttemptAt** *(property)* - When the renewal service last attempted issuance (UTC).
- **LastAttemptSucceeded** *(property)* - Whether that attempt succeeded.
- **LastError** *(property)* - Error text from the last failed attempt.
- **NextCheckAt** *(property)* - When the renewal service will next check (UTC).
- **NotAfter** *(property)* - Certificate NotAfter (UTC).
- **NotBefore** *(property)* - Certificate NotBefore (UTC).
- **RenewalPending** *(property)* - Whether a renew-now request is waiting to be picked up.
- **StatusUpdatedAt** *(property)* - When the renewal service last wrote its status (UTC); stale means it is not running.
- **Subject** *(property)* - Certificate subject.
