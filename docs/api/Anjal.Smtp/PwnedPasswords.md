# PwnedPasswords

**Namespace:** `Anjal.Smtp`

The leaked-password checks of the running service (rc.15, item 35; owner, 7 Oct 2026: "both"). Every new password is checked twice: against the downloaded list (passwords seen in 3 or more leaks, refreshed every six months) and online against Have I Been Pwned's full, current list, by k-anonymity - only the first 5 of the 40 characters of the password's SHA-1 fingerprint are sent, the answer holds every leaked fingerprint starting with them (padded to a uniform size), and the match is made here. When the online service cannot be reached the downloaded list's answer stands, so a password change is never held up. ANJAL_PWNED_MODE chooses: "both" (the default), "download" (nothing is sent at password time: for a customer whose servers may not reach the internet), "online", or "off". The service names the list file once at start-up; when a refresh replaces it, the new one is picked up within a minute, by the server and the webmail alike. Without a file (before the first download finishes) the online check and the short list built into apply.

## Members

- **Clear** *(method)* - Stop checking against a file (tests).
- **IsLeaked** *(method)* - True when the password is on the downloaded leaked-password list.
- **IsLeakedOnlineAsync** *(method)* - Ask Have I Been Pwned whether the password has leaked, by k-anonymity: only the first 5 characters of its SHA-1 fingerprint are sent; the full fingerprint is matched here, against every leaked one in the answer. Waits at most 3 seconds.
- **Reload** *(method)* - Look at the file again now, rather than within the minute (after a refresh in this process).
- **Use** *(method)* - Check passwords against the list in this file from now on.
- **ChecksOnline** *(property)* - True when passwords are also checked online (mode "both" or "online").
- **ConfiguredPath** *(property)* - The file named by ANJAL_PWNED_FILE, or .
- **Current** *(property)* - The list in use, or null when there is none yet.
- **DefaultPath** *(property)* - The usual place for the list: /var/lib/anjal on Linux, the local application data folder elsewhere.
- **Mode** *(property)* - "both", "download", "online" or "off", from ANJAL_PWNED_MODE (default "both").
- **OnlineBaseUrl** *(property)* - The range API for the online check (tests set a stand-in).
- **OnlineClient** *(property)* - The HTTP client for the online check (tests set a stand-in; null restores the usual one).
- **OnlineMinimumCount** *(property)* - The fewest leaks that make a password refused by the online check, from ANJAL_PWNED_ONLINE_MIN_COUNT (default 1: any leak).
- **UsesDownload** *(property)* - True when the downloaded list is used (mode "both" or "download").
