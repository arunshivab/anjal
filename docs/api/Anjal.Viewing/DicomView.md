# DicomView

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §3.4; rc.30 on SIAARA v0.1.0-rc.1): a DICOM file's preview - its outcome, and the caption fields as text from the file (escaped when shown). The patient's name and ID are never here. The picture travels as the answer's first file, and for several frames the strip (the frames side by side, one picture) as the second.

## Members

- **Columns** *(property)* - The columns.
- **Frames** *(property)* - The frames.
- **Incomplete** *(property)* - True when the file was cut off or damaged: always shown.
- **Modality** *(property)* - The modality.
- **Outcome** *(property)* - Ok, NotDicom, NoPixelData, Unsupported, TooLarge or Damaged.
- **Reason** *(property)* - Why there is no picture, from the toolkit.
- **Rows** *(property)* - The rows.
- **Series** *(property)* - The series description.
- **StripFrames** *(property)* - rc.30: the strip of several frames is the answer's second file; how many frames it shows.
- **StudyDate** *(property)* - The study date.
- **TransferSyntax** *(property)* - The transfer syntax's name.
