# GreylistOptions

**Namespace:** `Anjal.Spam`

Settings for .

## Members

- **Delay** *(property)* - How long a new (IP, sender, recipient) triplet is deferred. Default 5 minutes.
- **MaxEntries** *(property)* - Most triplets remembered. Past it the table is swept immediately and, if still full, the least recently seen triplets are forgotten - which only means those senders are greylisted once more. Default 200,000.
- **PassedLifetime** *(property)* - How long a triplet that passed stays whitelisted after its last use. Default 36 hours.
- **PendingLifetime** *(property)* - How long a triplet that never retried is remembered before being forgotten. Default 8 hours.
