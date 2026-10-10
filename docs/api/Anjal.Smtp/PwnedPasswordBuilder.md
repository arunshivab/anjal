# PwnedPasswordBuilder

**Namespace:** `Anjal.Smtp`

Builds the leaked-password list file (rc.15, item 35): from Have I Been Pwned's range API (the way the official Pwned Passwords downloader fetches it - a million small requests, each for the fingerprints starting with five given hex digits; no password or user detail is sent), or from the single file the official downloader writes. Only passwords seen in at least the given number of leaks are kept. The new file is written beside the old under a temporary name and swapped in only when complete.

## Members

- **DefaultMinimumCount** *(field)* - The fewest leaks a password needs to be kept, by default (owner, 7 Oct 2026).
- **LastRange** *(field)* - The last of the 1,048,576 ranges (five hex digits).
- **RangeApi** *(field)* - Have I Been Pwned's range API.
- **BuildFromApiAsync** *(method)* - Download the list from the range API and write the file.
- **BuildFromTextAsync** *(method)* - Write the file from the text the official Pwned Passwords downloader writes as one file (one "SHA1:count" line per leaked password, sorted). For building on another computer and copying the result to the server.
- **ParseRange** *(method)* - The fingerprints of one range seen in at least leaks, sorted: its five hex digits followed by each line's 35.
- **ConfiguredMinimumCount** *(property)* - The fewest leaks set by ANJAL_PWNED_MIN_COUNT, or .
