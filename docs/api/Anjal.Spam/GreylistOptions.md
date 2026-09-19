# GreylistOptions

**Namespace:** `Anjal.Spam`

Settings for .

## Members

- **Delay** *(property)* - How long a new (IP, sender, recipient) triplet is deferred. Default 5 minutes.
- **PassedLifetime** *(property)* - How long a triplet that passed stays whitelisted after its last use. Default 36 hours.
- **PendingLifetime** *(property)* - How long a triplet that never retried is remembered before being forgotten. Default 8 hours.
