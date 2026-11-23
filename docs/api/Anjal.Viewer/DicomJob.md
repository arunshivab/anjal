# DicomJob

**Namespace:** `Anjal.Viewer`

rc.30 (DES-22 §3.4 and §18.3; D-372, AC-19, D-386, D-366): DICOM from SIAARA v0.1.0-rc.1 - a file's preview, its patient fields on request, and a ZIP's files grouped by patient, study and series. Every member used here was checked against the package's XML docs. Run only in a single-use child: the toolkit's reads have no time limit of their own, so the supervisor's deadline and memory stop are what bound them.

## Members

- **MaxText** *(field)* - The longest caption field kept (DES-22 §3.4: each cut at 256 characters).
- **Group** *(method)* - AC-19: the ZIP's own DICOM files grouped by patient, study and series, with its DICOMDIR when it has one. The listing is read once; each file is unpacked with the archive viewer's caps, given to the grouping (which reads it header-only and keeps none of it), and let go before the next.
- **Patient** *(method)* - D-366: Patient's Name and Patient ID, read header-only with the preview's limits - only for Show patient details, which the webmail offers to the mailbox's own person and logs.
- **PersonName** *(method)* - A DICOM Person Name as people write it: "FAMILY^Given^Middle^Prefix^Suffix" becomes "FAMILY, Prefix Given Middle Suffix", from its first group (the ones after "=" are the same name in other scripts or spellings).
- **Run** *(method)* - Previews a DICOM file: the caption, the picture and, for several frames, the strip.
- **GroupingOptions** *(property)* - The grouping's budgets: SIAARA's recommended ones for untrusted input, with the patient fields never read (D-366: patients are told apart without them).
