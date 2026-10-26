# MailboxKeyDirectory

**Namespace:** `Anjal.Mailbox`

rc.25 (DES-15 §5.2): mailboxes' public keys by address, for the Maildir store and the delivery sink. What is known of an address is asked again after a minute - "no key yet" (so mail arriving after a person's first sign-in is sealed) and a key alike (an address deleted and given to a new mailbox has another key).

## Members

- **#ctor** *(method)* - Initializes a new instance of the class.
- **Forget** *(method)* - Forget what is known of an address (its keys were just made in this process).
- **PublicKeyAsync** *(method)* - _(no description)_
