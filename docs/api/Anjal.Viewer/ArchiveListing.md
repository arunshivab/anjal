# ArchiveListing

**Namespace:** `Anjal.Viewer`

An archive's listing, or why there is none: a password needed or wrong, or a refusal.

## Members

- **#ctor** *(method)* - An archive's listing, or why there is none: a password needed or wrong, or a refusal.
- **Of** *(method)* - A listing.
- **Of** *(method)* - No listing, for a status (a password needed or wrong, or the archive damaged).
- **Refuse** *(method)* - A refused archive ().
- **Listed** *(property)* - True when the entries are listed.
- **Place** *(property)* - For a password: which archive it is for ("" the attachment, "3" the one inside it).
- **Status** *(property)* - Ok, or the status to answer with.
- **View** *(property)* - The listing (with set when refused).
