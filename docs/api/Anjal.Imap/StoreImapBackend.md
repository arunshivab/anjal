# StoreImapBackend

**Namespace:** `Anjal.Imap`

rc.28 (DES-20): the IMAP server's view of Anjal's own store - people sign in with app passwords only, their keys open from the app's own copy, and every change goes through the same index and Maildir the webmail uses.

## Members

- **Hidden** *(field)* - Folders the webmail keeps to itself.
- **UseAppPassword** *(field)* - What an app is told when it sends the account password.
- **#ctor** *(method)* - Construct.
- **LoginAsync** *(method)* - _(no description)_
