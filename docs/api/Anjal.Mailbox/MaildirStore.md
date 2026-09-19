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
- **SafeSegment** *(method)* - Make a path segment safe: reject separators and traversal so a crafted tenant slug or address can never escape the root.
- **SetFlags** *(method)* - _(no description)_
- **StripFlags** *(method)* - Strip a :2,flags (or Windows ;2,flags) suffix from a Maildir file name, returning the unique base name.
- **TrySplitRelative** *(method)* - Validate and split a relative path of the form new/<name> or cur/<name>, rejecting anything that could escape the folder directory.
- **WriteAsync** *(method)* - _(no description)_
- **DefaultRoot** *(property)* - The default Maildir root for this platform when nothing is configured: /var/mail/anjal on Unix, %LOCALAPPDATA%\Anjal\mail on Windows.
- **Root** *(property)* - _(no description)_
