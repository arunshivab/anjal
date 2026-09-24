# PasswordPolicy

**Namespace:** `Anjal.Smtp`

What a password must be, for a mailbox, an SMTP service account, or a password change in the webmail. One policy, applied everywhere: the three paths used to disagree (12 characters in the webmail, 8 in the admin API). The rule is the owner's: at least 8 characters with an upper-case letter, a lower-case letter, a digit and a symbol - plus a refusal of the predictable results of that rule. Composition rules alone produce "Apulki@123" and "Passw0rd!", which are among the first an attacker tries, so length and the blocklist do the real work.

## Members

- **MaximumLength** *(field)* - The longest accepted; long passphrases are welcome, unbounded input is not.
- **MinimumLength** *(field)* - The shortest password accepted.
- **NameSeparators** *(field)* - Passwords refused however well they satisfy the composition rule, and the words that may not make up most of one. Lower-case; comparison ignores case, digits and symbols, so "Apulki@123" is caught by "apulki".
- **PassphraseLength** *(field)* - At or above this length the four-class rule is not required. A passphrase such as "ward round tuesday tea" is far stronger than "Apulki@123", and refusing it would push people towards the short, predictable shapes the classes encourage. The blocklist still applies.
- **Check** *(method)* - Check a password. Returns null when it is acceptable, or a sentence for the user saying what is wrong - never a list of rules they have already satisfied.
- **IsARun** *(method)* - "abcdefg1!" or "7654321a!" - a straight run through the alphabet or digits.
- **IsOneRepeatedCharacter** *(method)* - "aaaaaaa", and so "Aaaaaaa1!" once digits and symbols are set aside.
- **PartsOf** *(method)* - The meaningful words of a name or address: 4 letters or more.
