# DkimSealKey

**Namespace:** `Anjal.Server`

DES-11 S6 (owner, 10 Oct 2026, "A"): the mail server's seal key. Its private half stays in one file on this server (ANJAL_DKIM_SEAL_KEY; made at the first start) and goes into the encrypted backup; its public half is published in the database, where the webmail reads it to lock each DKIM key it makes. Only this process can open those keys, to sign.

## Members

- **PrepareAsync** *(method)* - Load the seal key, or make it the first time; publish its public half; and lock every DKIM key still kept unlocked (the one-time step). Returns the key, or null when it could not be read or made (the reason is logged; signing then uses only keys not sealed).
- **ConfiguredPath** *(property)* - The configured path.
- **DefaultPath** *(property)* - Where the private half is kept when ANJAL_DKIM_SEAL_KEY is not set.
