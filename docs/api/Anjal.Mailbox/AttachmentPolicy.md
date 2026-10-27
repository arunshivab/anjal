# AttachmentPolicy

**Namespace:** `Anjal.Mailbox`

The rules for files on incoming mail, as accepted 2 Oct 2026 (rc.22, item 39): programs and scripts are refused; disk images go to Junk; every other file is scanned by ClamAV, a virus is removed and the recipient told; if the scanner is down, the mail is held in quarantine and the operator told at once and every 30 minutes. The rules are the same for every organisation.

## Members

- **HeldHeader** *(field)* - The header on a held message: why.
- **MailWarningHeader** *(field)* - rc.24 (item 94): the header on a message ClamAV flagged as a whole: the signature's name.
- **QuarantineFolder** *(field)* - The folder held mail waits in; never shown as a folder of the person's own.
- **RemovedHeader** *(field)* - The header on a message a file was removed from: the file names and the virus.
- **DiskImagesIn** *(method)* - The files of a message that are disk images, by name or inside an archive (rc.26, item 85).
- **FileNameOf** *(method)* - The file name of a part: its Content-Disposition filename, else its Content-Type name, else empty.
- **IsDiskImage** *(method)* - Whether a file name is a disk image, by its ending.
- **IsProgram** *(method)* - Whether a file name is a program or script, by its ending.
- **IsProgramContent** *(method)* - Whether a file's bytes are a program, whatever its name says (rc.26, item 85, D-173): a Windows program (MZ), a Linux one (ELF), a Mac one (Mach-O), a Java class, a script that names its own interpreter (#!), or an archive holding a program (a ZIP, a JAR among them).
- **JudgeAsync** *(method)* - Judge a message's files: the name rules first, then the scanner (when there is one) on every file that is not plain text. A message without files is clean at once.
- **MarkWholeMail** *(method)* - The message with any it arrived with removed, and Anjal's own added at the top when is set (rc.24, item 94). The same array comes back when there is nothing to change, so a message is rewritten only when it must be.
- **ProgramsIn** *(method)* - The files of a message that are programs or scripts - by name, by content, or inside an archive (rc.26, item 85).
- **RefusedInside** *(method)* - Inside the organisation (rc.26, item 85, D-173): programs, scripts and disk images are refused for mail written here too - webmail, a mail app on port 587, an application. Null when the message may go; else the sentence for the sender.
- **RisksIn** *(method)* - rc.26 (DES-14 §13, D-196): the files of a message that carry a risk the reader warns about - a locked archive or document, or one with macros - for an application inbox's data.
