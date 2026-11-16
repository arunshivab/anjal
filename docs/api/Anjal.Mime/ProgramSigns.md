# ProgramSigns

**Namespace:** `Anjal.Mime`

rc.26 (item 85, D-173), moved here in rc.29: how a file is known to be a program - by its name, by its first bytes, or by what a ZIP holds. The mail server's attachment rules (AttachmentPolicy) and the separate viewer process (AC-17) use the same rules from here, and the viewer needs nothing else of the mail libraries.

## Members

- **IsOfficeContainer** *(method)* - Whether a ZIP's entry names are an Office document's (it holds [Content_Types].xml).
- **IsProgramContent** *(method)* - Whether a file's bytes are a program, whatever its name says: a Windows program (MZ), a Linux one (ELF), a Mac one (Mach-O), a Java class, a script that names its own interpreter (#!), or an archive holding a program (a ZIP, a JAR among them).
- **IsProgramName** *(method)* - Whether a file name is a program or script, by its ending.
- **ZipEntryNames** *(method)* - The names of the entries of a ZIP, read from its local headers; empty for anything else.
