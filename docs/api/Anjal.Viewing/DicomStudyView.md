# DicomStudyView

**Namespace:** `Anjal.Viewing`

rc.30 (AC-19, D-386; DES-22 §18.3): the DICOM files of a ZIP grouped by patient, study and series with SIAARA's grouping (and the ZIP's DICOMDIR, when it has one). No patient's name or ID: patients are told apart by number.

## Members

- **Budget** *(property)* - The grouping's limit that was reached (the toolkit's name for it), or null when none was.
- **Files** *(property)* - DICOM files the ZIP holds (by their mark).
- **FilesRead** *(property)* - Files the grouping read.
- **Incomplete** *(property)* - Files whose header could not be read whole (ReadLoss above 0).
- **Issues** *(property)* - What the grouping found worth saying, counted by kind (the toolkit's names).
- **Patients** *(property)* - The patients, in the toolkit's order.
- **Skipped** *(property)* - Files of the ZIP not read for the grouping: too large, refused or unreadable.
- **Ungrouped** *(property)* - Files that could not be put in a series.
