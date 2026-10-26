# MailKeyring

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §4): the keys unlocked for the work in hand - a person's request in the webmail (their mailbox key and data key, and those of the shared mailboxes they belong to), or a test. It is ambient for the async flow: sets it for a scope, and the storage layer (the Maildir store, the message rows) opens what it can with . Nothing in it is ever written anywhere; it ends with the request.

## Members

- **#ctor** *(method)* - Initializes a new instance of the class, empty.
- **#ctor** *(method)* - Initializes a new instance of the class that also finds the keys of another (not owned).
- **Add** *(method)* - Add a mailbox's unlocked keys.
- **AddPrivateKey** *(method)* - Add a private key that belongs to no mailbox (an organisation's recovery key, the break-glass key).
- **AddSymmetricKey** *(method)* - Add a symmetric key that belongs to no mailbox (the webmail's key for mail waiting to be sent).
- **DataKey** *(method)* - The data key of an unlocked mailbox, or null.
- **Dispose** *(method)* - _(no description)_
- **Has** *(method)* - Whether this mailbox's keys are unlocked here.
- **PrivateKey** *(method)* - _(no description)_
- **PrivateKeyFor** *(method)* - A private key added with (PKCS#8), by the id of its public key, or null.
- **PrivateKeyOf** *(method)* - The private key (PKCS#8) of an unlocked mailbox, or null - for re-wrapping it (a new password, a shared mailbox's member).
- **SymmetricKey** *(method)* - _(no description)_
- **Use** *(method)* - Make this keyring the current one until the returned scope is disposed.
- **Current** *(property)* - The keyring of the current flow, or an empty one.
- **Empty** *(property)* - A keyring with nothing in it (the server, a background task, an operator).
- **Mailboxes** *(property)* - The mailboxes whose keys are here.
