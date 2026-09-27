# EvidenceVault

**Namespace:** `Anjal.Mailbox`

The files of the evidence store (v1.0.0-rc.8, ANJAL-DES-01): root/YYYY/MM/DD/<id>.eml, written once and never replaced. A file is written to a temporary name, flushed to disk, made read-only, then renamed into place - so a file that exists is always complete.

## Members

- **#ctor** *(method)* - Construct.
- **Delete** *(method)* - Remove an evidence file (retention purge only). Missing files are ignored.
- **Exists** *(method)* - Whether an evidence file exists.
- **FullPath** *(method)* - A path under the root, refusing anything that would leave it.
- **ReadAsync** *(method)* - Read an evidence file.
- **Sha256Hex** *(method)* - SHA-256 of bytes, lower-case hex.
- **WriteAsync** *(method)* - Write a new evidence file. Throws if it cannot be written, or if it already exists.
- **DefaultRoot** *(property)* - The default evidence root when nothing is configured: /var/lib/anjal/evidence on Unix, %LOCALAPPDATA%\Anjal\evidence on Windows.
- **Root** *(property)* - The evidence root.
