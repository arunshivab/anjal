# ViewStatus

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §5): how a job went. The webmail turns each into words; none carries a file's text.

## Members

- **Busy** *(field)* - The person already has as many jobs running as they may, or the queue is full.
- **Damaged** *(field)* - The file is damaged.
- **Failed** *(field)* - Anything else that went wrong: "This file could not be shown".
- **NeedsPassword** *(field)* - The file needs a password.
- **Ok** *(field)* - Done.
- **Refused** *(field)* - An archive (or a file inside one) Anjal does not open: too many files, too large, a program.
- **TooLarge** *(field)* - Too large or too complex to show here (a library's limit, or 64 MB).
- **TooSlow** *(field)* - It took too long and its process was stopped.
- **Unavailable** *(field)* - The viewer process is not there.
- **Unsupported** *(field)* - The viewer does not show this kind of file.
- **WrongOrDamaged** *(field)* - AC-20: a file from an archive locked with the old ZipCrypto method failed after its password passed the method's one-byte check - the password is wrong, or the file is damaged; the two cannot be told apart.
- **WrongPassword** *(field)* - The password given does not open it.
