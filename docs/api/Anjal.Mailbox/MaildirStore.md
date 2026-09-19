# MaildirStore

**Namespace:** `Anjal.Mailbox`

Default backed by .

## Members

- **#ctor** *(method)* - Construct with the root directory. The directory is created on first write if it does not already exist.
- **EnsureFolder** *(method)* - _(no description)_
- **FolderPath** *(method)* - Compute the Maildir directory for a folder without creating it.
- **NextUniqueName** *(method)* - Generate a Maildir unique file name: <seconds>.M<microseconds>P<pid>Q<sequence>.<host>. The combination of time, process id and a process-wide counter makes names unique even for two deliveries in the same microsecond.
- **ReadAsync** *(method)* - _(no description)_
- **SafeSegment** *(method)* - Make a path segment safe: reject separators and traversal so a crafted tenant slug or address can never escape the root.
- **WriteAsync** *(method)* - _(no description)_
- **DefaultRoot** *(property)* - The default Maildir root for this platform when nothing is configured: /var/mail/anjal on Unix, %LOCALAPPDATA%\Anjal\mail on Windows.
- **Root** *(property)* - _(no description)_
