# AcmeRenewalService

**Namespace:** `Anjal.Acme`

Obtains and renews the certificate. Exactly one process per deployment should run this; every process may read the resulting files through a . The loop: ensure the account, check the stored certificate, renew when it is missing, within of expiry, covers different domains, or a renew-now marker was dropped; on failure, back off exponentially and keep the old certificate in place.

## Members

- **#ctor** *(method)* - Construct.
- **NeedsRenewal** *(method)* - Whether the stored certificate needs replacing: missing, expiring within , or issued for a different set of domains.
- **RenewNowAsync** *(method)* - Run one full issuance now, regardless of expiry. Returns the new certificate. Throws on failure; the previous certificate stays in place.
- **RunAsync** *(method)* - Run the renewal loop until cancelled. Safe to host in any process that serves on port 80 for the configured domains.
- **Status** *(property)* - The current status snapshot.
