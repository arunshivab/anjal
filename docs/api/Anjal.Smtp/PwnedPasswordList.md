# PwnedPasswordList

**Namespace:** `Anjal.Smtp`

The downloaded list of passwords known from data leaks (rc.15, item 35; owner's decisions of 7 Oct 2026): Have I Been Pwned's Pwned Passwords, kept on the server as a compact file and refreshed every six months, alongside an online check of every new password (see ). Anjal is built first for applications, transactional mail and hospital systems, where sign-in has a second step, so the list keeps the passwords seen in at least three leaks (ANJAL_PWNED_MIN_COUNT, default 3) - about 1.1 billion of the 2.1 billion, in about 5.8 GB. Checking against this list sends nothing anywhere. Each leaked password is kept as the first 8 bytes of its SHA-1 fingerprint. The file groups them by their first 3 bytes, so only the other 5 are stored: a 32-byte header ("ANJALPW2", the number of fingerprints, when it was built, the fewest leaks a password needed to be included), then where each of the 16,777,216 groups starts (4 bytes each, plus one for the end), then the fingerprints' last 5 bytes, sorted. A password that was never leaked is wrongly refused about once in 16 billion. A check reads about ten small pieces of the file through the system's file cache; the file is never loaded whole.

## Members

- **EntriesStart** *(field)* - Where the stored fingerprints begin: after the header and the group starts.
- **EntryLength** *(field)* - The bytes kept of each fingerprint (its first 3 are its group).
- **Groups** *(field)* - How many groups the fingerprints are sorted into (by their first 3 bytes).
- **HeaderLength** *(field)* - The size of the header, in bytes.
- **Magic** *(field)* - The first bytes of every list file.
- **Contains** *(method)* - True when the password is on the list.
- **ContainsFingerprint** *(method)* - True when the fingerprint is on the list. The file is opened for each check and closed again: passwords are checked rarely, and holding nothing open lets a refresh replace the file on every operating system.
- **Fingerprint** *(method)* - The fingerprint Anjal keeps for a password: the first 8 bytes of its SHA-1, as a number.
- **Open** *(method)* - Open a list file, checking its header and size.
- **SizeFor** *(method)* - The size a list of this many fingerprints has.
- **BuiltAt** *(property)* - When the list was built.
- **Count** *(property)* - How many leaked passwords the list holds.
- **MinimumCount** *(property)* - The fewest leaks a password needed to be included.
- **Path** *(property)* - Where the list was read from.
