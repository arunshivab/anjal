# PasswordPolicy

**Namespace:** `Anjal.Smtp`

What a password must be, for a mailbox, an SMTP service account, or a password change in the webmail. One policy, applied everywhere: the three paths used to disagree (12 characters in the webmail, 8 in the admin API). Owner, 10 Oct 2026 (P1-P8): the rules meet NIST SP 800-63B-4, OWASP ASVS 4.0.3 and 5.0, and PCI DSS 4.0 at once. A password used alone is at least 15 characters; with two-step sign-in, at least 12. There is no rule about kinds of characters (NIST forbids them: they produce "Apulki@123"); length, the list of leaked passwords and the refusal of guessable words do the work. Length counts characters (Unicode code points), after the password is normalised (NFKC), so a Tamil or Hindi letter is never counted twice.

## Members

- **AloneLength** *(field)* - The shortest password used alone, without two-step sign-in (NIST SP 800-63B-4).
- **Forbidden** *(field)* - Words that make a password easy to guess: common words and this product's and its customers' names. A password made up mostly of them is refused (owner, 10 Oct 2026, P4) - a long phrase that happens to contain one ("snail mail on the garden path") is not. Lower-case; compared with digits and symbols set aside, so "Apulki@123" is "apulki".
- **Leaked** *(field)* - Passwords seen again and again in published leaks (rc.13), as their letters alone: "Krishna@123" and "krishna1" are both "krishna". A password whose letters are exactly one of these is refused.
- **LeakedMessage** *(field)* - What a person is told when their new password is a known leaked one (rc.15, item 35).
- **MaximumLength** *(field)* - The longest accepted; long passphrases are welcome, unbounded input is not.
- **MinimumLength** *(field)* - The shortest password accepted anywhere: with two-step sign-in on (owner, 10 Oct 2026).
- **CharacterCount** *(method)* - How many characters a password has: Unicode code points, after normalising.
- **Check** *(method)* - Check a password. Returns null when it is acceptable, or a sentence for the user saying what is wrong - never a list of rules they have already satisfied.
- **CheckAsync** *(method)* - Check a new password fully (rc.15, item 35): , and then Have I Been Pwned's full, current list online (by k-anonymity; see ). When the online service cannot be reached the password is judged without it.
- **Compact** *(method)* - Letters and digits only, lower-case: "Pass-1234 5678" is "pass12345678".
- **Covered** *(method)* - How many characters of the text are inside any of the words.
- **Evaluate** *(method)* - Each rule, judged on its own (rc.13): the list shown beside a new password, ticked as the person types.
- **IsARun** *(method)* - "abcdefghijklmno" or "987654321098" - a straight run through the alphabet or the digits.
- **IsCommon** *(method)* - A known leaked password by its letters, one repeated character, or a straight run.
- **IsOneRepeatedCharacter** *(method)* - "aaaaaaa", and so "Aaaaaaa1!" once digits and symbols are set aside.
- **LengthFor** *(method)* - The shortest password for a person, with or without two-step sign-in.
- **MostlyWords** *(method)* - True when the given words cover at least half of the password's letters (owner, 10 Oct 2026, P4: compare the whole password, not any part of it). Words of letters are matched against its letters; words of digits against its letters and digits.
- **Normalize** *(method)* - The password as it is checked and hashed: Unicode normalised (NFKC).
- **PartsOf** *(method)* - The meaningful words of a name or address: 4 letters or more.
- **PersonalWords** *(method)* - The meaningful words of the person's address and name.
