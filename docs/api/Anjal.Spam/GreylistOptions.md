# GreylistOptions

**Namespace:** `Anjal.Spam`

Settings for .

## Members

- **Delay** *(property)* - How long a new (IP, sender, recipient) triplet is deferred. Default 5 minutes.
- **LoadFromStore** *(property)* - Reads the remembered triplets from the database (v1.0.0-rc.7). When set, the database is the primary record and is its mirror: at start-up the database is read first, and the file only if the database cannot be read or holds nothing (owner's decision, 27 Sep 2026: "if the database cannot recover, we can use the file").
- **Log** *(property)* - Where each decision is written (DEF-059: before rc.5 no deferral was logged, so a delayed message could not be told from a lost one).
- **MaxEntries** *(property)* - Most triplets remembered. Past it the table is swept immediately and, if still full, the least recently seen triplets are forgotten - which only means those senders are greylisted once more. Default 200,000.
- **PassedLifetime** *(property)* - How long a triplet that passed stays remembered after its last use. Default 35 days (decision 2B, 26 Sep 2026): at 36 hours a colleague who wrote weekly was delayed every week.
- **PendingLifetime** *(property)* - How long a triplet that never retried is remembered before being forgotten. Default 8 hours.
- **SaveInterval** *(property)* - How often, at most, the state file is rewritten. Default one minute.
- **SaveToStore** *(property)* - Writes the remembered triplets to the database whenever the file is written. It runs off the mail-handling path, so a slow or unavailable database never delays mail; a failure is logged and retried at the next save.
- **StateFile** *(property)* - File the remembered triplets are kept in, so a restart or upgrade does not greylist every sender again; null keeps them in memory only. The file is rebuildable: losing it only means senders are greylisted once more.
- **TrustedSender** *(property)* - Optional check that exempts a sender from greylisting: given the client IP and the MAIL FROM address, true lets the message through at once. The server uses "the IP passes SPF for the MAIL FROM domain".
