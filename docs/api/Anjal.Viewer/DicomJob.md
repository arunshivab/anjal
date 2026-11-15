# DicomJob

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §3.4, D-372): a DICOM file's preview, from the Siaara package. Until a Siaara package is on the feed the code that calls it is not compiled (it is built with -p:Siaara=true once it is), and the switch that offers DICOM in the webmail stays off: this answers "unsupported".

## Members

- **Run** *(method)* - Previews a DICOM file.
- **Available** *(property)* - True when this build carries the Siaara preview.
