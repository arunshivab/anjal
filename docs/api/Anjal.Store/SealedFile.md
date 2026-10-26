# SealedFile

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §7.10): a large file kept encrypted on disk - a person's data download - written and read in pieces, so that a mailbox of several gigabytes is never held in memory whole. Each piece of up to 1 MiB is its own envelope (); the file ends with an empty piece, so a file cut short is found. "ANJX" | version 1 | ( piece length (u32, big-endian) | envelope ) ... | 0 (u32)

## Members

- **PieceSize** *(field)* - The largest piece of plain bytes in one envelope.
- **IsSealed** *(method)* - Whether a file begins as an encrypted file.
- **OpenToAsync** *(method)* - Decrypt an encrypted file into .
- **Writer** *(method)* - A stream that encrypts what is written to it into , piece by piece, with a symmetric key.
