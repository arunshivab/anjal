# MaildirStore

**Namespace:** `Anjal.Mailbox`

Default backed by .

## Members

- **#ctor** *(method)* - Construct with the root directory. The directory is created on first write if it does not already exist.
- **Delete** *(method)* - _(no description)_
- **EnsureFolder** *(method)* - _(no description)_
- **FlagSuffix** *(method)* - Compose the Maildir flag suffix (:2, followed by flags in ASCII order) for the given flags. Empty flags still yield :2,.
- **FolderPath** *(method)* - Compute the Maildir directory for a folder without creating it.
- **Move** *(method)* - _(no description)_
- **NextUniqueName** *(method)* - Generate a Maildir unique file name: <seconds>.M<microseconds>P<pid>Q<sequence>.<host>. The combination of time, process id and a process-wide counter makes names unique even for two deliveries in the same microsecond.
- **ReadAsync** *(method)* - _(no description)_
- **ReadStoredAsync** *(method)* - The stored bytes of a message as they are on disk (an envelope for an organisation's mailbox), for the session's housekeeping. Null when the file is gone.
- **ReplaceStoredAsync** *(method)* - Replace a message's stored bytes in place, under the same name (written under tmp/ and renamed over the old file, so a reader never sees a partial file). For sealing existing mail (the move) and re-keying (DES-15 §5.3). False when the file is gone.
- **SafeSegment** *(method)* - Make a path segment safe: reject separators and traversal so a crafted tenant slug or address can never escape the root.
- **SetFlags** *(method)* - _(no description)_
- **StripFlags** *(method)* - Strip a :2,flags (or Windows ;2,flags) suffix from a Maildir file name, returning the unique base name.
- **TrySplitRelative** *(method)* - Validate and split a relative path of the form new/<name> or cur/<name>, rejecting anything that could escape the folder directory.
- **WriteAsync** *(method)* - _(no description)_
- **DefaultRoot** *(property)* - The default Maildir root for this platform when nothing is configured: /var/mail/anjal on Unix, %LOCALAPPDATA%\Anjal\mail on Windows.
- **HoldFolder** *(property)* - The folder whose mail is sealed with .
- **HoldKey** *(property)* - rc.25 (DES-15 §7.4): the webmail's own key for mail waiting in the Scheduled folder, which it must send by itself at the time chosen, when nobody is signed in. Such mail is sealed with this key instead of the mailbox's; it is re-sealed to the mailbox if it goes back to Drafts.
- **Keys** *(property)* - rc.25 (DES-15 §5.2): where the public keys of organisations' mailboxes come from. When set, a message written for a mailbox with a key is sealed to it before it touches the disk; reading opens it with the keys of the current flow (). Unset (tests, development), files are written as given.
- **Root** *(property)* - _(no description)_
