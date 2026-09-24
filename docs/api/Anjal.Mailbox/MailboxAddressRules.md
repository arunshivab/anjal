# MailboxAddressRules

**Namespace:** `Anjal.Mailbox`

What a mailbox address may contain. The local part becomes a directory name under the mail root, so it is checked strictly and up front, before anything is written: "../evil@x.test" or "a b@x.test" used to pass a looser check, commit the database row, and then fail while building the maildir, leaving an enabled mailbox with nowhere to put mail (DEF-043).

## Members

- **MaxLocalPart** *(field)* - The longest local part accepted (RFC 5321 4.5.3.1.1).
- **IsValidLocalPart** *(method)* - Whether a local part is acceptable: 1 to 64 characters of ASCII letters, digits, and . _ % + -, with no leading, trailing or doubled dot. Deliberately narrower than RFC 5321 permits - quoted forms and the other specials are legal in mail but make poor directory names, and nobody needs them for a hospital mailbox.
- **TryValidate** *(method)* - Whether a whole address is acceptable: a valid local part and a real-shaped domain (the same check sender rules use).
