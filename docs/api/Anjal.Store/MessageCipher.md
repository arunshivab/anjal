# MessageCipher

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §3): a message row's subject, body text and preview - sealed to the mailbox's public key when it has one, opened with the keys of the current flow (). A row that cannot be opened here reads as empty text: nothing readable leaves the store.

## Members

- **ArrivalPrefix** *(field)* - The start of a stored field that arrived for the RSA key (kind 1) and is not yet re-keyed (§5.3).
- **RekeyedPrefix** *(field)* - The start of a stored field that is re-keyed to the data key (kind 2).
- **SnippetLength** *(field)* - How many characters of the body the list shows.
- **IsSealed** *(method)* - Whether a stored field is encrypted.
- **Matches** *(method)* - Whether a decrypted row's text contains the words searched for (any case).
- **Open** *(method)* - A stored field opened with the current keys: the text, or empty when it cannot be opened here.
- **Seal** *(method)* - The stored form of a new row's text.
- **SealWithKey** *(method)* - The stored form of text written in a session (the data key; no RSA).
- **SortKey** *(method)* - The key a subject sorts by: without "Re:", "Fwd:", "Aw:", in lower case.
