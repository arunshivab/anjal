# AcmeOptions

**Namespace:** `Anjal.Acme`

Configuration for .

## Members

- **CheckInterval** *(property)* - How often the service checks expiry and the renew-now marker. Default 1 hour.
- **ContactEmail** *(property)* - Contact email for the account (expiry notices), or empty.
- **DirectoryUrl** *(property)* - ACME directory URL. Default Let's Encrypt production.
- **Domains** *(property)* - DNS names for the certificate; the first is the subject CN.
- **InitialRetryDelay** *(property)* - Backoff after a failed attempt, doubled each failure up to . Default 5 minutes.
- **KeyType** *(property)* - Certificate key type.
- **MaxRetryDelay** *(property)* - Upper bound for the retry backoff. Default 6 hours.
- **RenewBefore** *(property)* - Renew when less than this remains before expiry. Default 30 days.
- **ValidationTimeout** *(property)* - How long to poll a challenge or order before giving up. Default 2 minutes.
