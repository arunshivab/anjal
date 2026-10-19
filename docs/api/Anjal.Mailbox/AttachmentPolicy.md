# AttachmentPolicy

**Namespace:** `Anjal.Mailbox`

The rules for files on incoming mail, as accepted 2 Oct 2026 (rc.22, item 39): programs and scripts are refused; disk images go to Junk; every other file is scanned by ClamAV, a virus is removed and the recipient told; if the scanner is down, the mail is held in quarantine and the operator told at once and every 30 minutes. The rules are the same for every organisation.

## Members

- **HeldHeader** *(field)* - The header on a held message: why.
- **QuarantineFolder** *(field)* - The folder held mail waits in; never shown as a folder of the person's own.
- **RemovedHeader** *(field)* - The header on a message a file was removed from: the file names and the virus.
- **FileNameOf** *(method)* - The file name of a part: its Content-Disposition filename, else its Content-Type name, else empty.
- **IsDiskImage** *(method)* - Whether a file name is a disk image, by its ending.
- **IsProgram** *(method)* - Whether a file name is a program or script, by its ending.
- **JudgeAsync** *(method)* - Judge a message's files: the name rules first, then the scanner (when there is one) on every file that is not plain text. A message without files is clean at once.
