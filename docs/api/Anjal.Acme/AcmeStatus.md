# AcmeStatus

**Namespace:** `Anjal.Acme`

Snapshot of the renewal service, also written to status.json.

## Members

- **ConsecutiveFailures** *(property)* - Consecutive failures since the last success.
- **DirectoryUrl** *(property)* - Directory URL in use.
- **Domains** *(property)* - Domains in the stored certificate.
- **ExpiresAt** *(property)* - Leaf expiry (UTC), or null.
- **HasCertificate** *(property)* - Whether a certificate is currently stored.
- **LastAttemptAt** *(property)* - When the last issuance attempt started (UTC), or null.
- **LastAttemptSucceeded** *(property)* - Whether the last attempt succeeded.
- **LastError** *(property)* - Error text from the last failed attempt, or empty.
- **NextCheckAt** *(property)* - When the service will next check (UTC).
- **UpdatedAt** *(property)* - When the service last wrote this status (UTC).
