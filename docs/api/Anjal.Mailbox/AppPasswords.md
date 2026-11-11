# AppPasswords

**Namespace:** `Anjal.Mailbox`

rc.28 (DES-20 §2): passwords made for mail apps. Sixteen letters, four groups of four, from an alphabet with no look-alikes; kept as a PBKDF2 hash like account passwords; for an encrypted mailbox each one wraps its own copy of the mail key (with its own salt and the pepper), so removing it removes the copy.

## Members

- **MaxName** *(field)* - The longest name for a device.
- **MaxPerPerson** *(field)* - At most this many per person (D-347).
- **LooksLikeOne** *(method)* - Whether what was typed has the shape of an app password (sixteen letters).
- **Make** *(method)* - Make the row for a new app password.
- **Match** *(method)* - Which of a mailbox's app passwords was typed; null when none.
- **NewPassword** *(method)* - A new app password, as shown to the person once ("kfmt-wqpz-rbhd-snxa").
- **Normalize** *(method)* - The password as checked: letters only, lowercase (spaces and dashes an app kept are ignored).
- **OpenKeyring** *(method)* - A keyring holding a mailbox's keys, opened with an app password: the private key from the app's copy, and the mailbox's data key opened with it. Null when the mailbox has keys and they do not open.
- **Unlock** *(method)* - Open the mail key copy an app password wraps.
