# ChildJobs

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §4.1): the one job a child process does. A file inside an archive (AC-17) is unpacked here, in memory, and then shown by the same viewer as a file attached on its own.

## Members

- **Run** *(method)* - Does one job. Exceptions of the libraries become statuses; anything else is the caller's to catch.
- **Run** *(method)* - Runs one job over its files: the attachment first, then (for the editor) the joined PDFs and the pictures.
