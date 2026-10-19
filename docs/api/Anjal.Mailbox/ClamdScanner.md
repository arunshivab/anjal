# ClamdScanner

**Namespace:** `Anjal.Mailbox`

ClamAV's clamd, spoken to over its socket with the INSTREAM command (rc.22, item 39; decisions of 2 Oct 2026). Nothing leaves the server: the file's bytes go to clamd on this machine and back comes "OK" or the virus's name. Set with ANJAL_CLAMAV: "host:port" (clamd's TCP socket, usually 127.0.0.1:3310) or the path of its Unix socket (/run/clamav/clamd.ctl on Debian).

## Members

- **Setting** *(field)* - The setting that names the scanner.
- **Timeout** *(field)* - The longest wait for clamd to answer one file.
- **#ctor** *(method)* - Make a scanner for an endpoint.
- **FromSetting** *(method)* - The scanner the setting names, or null when the setting is empty (scanning off).
- **ScanAsync** *(method)* - _(no description)_
- **VersionAsync** *(method)* - _(no description)_
