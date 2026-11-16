# ViewFrames

**Namespace:** `Anjal.Viewing`

rc.29 (DES-22 §4.2): how a request or an answer travels - over the socket between the webmail and the viewer, and over the pipes between the viewer and a child. One frame: the mark "AVW1", the JSON's length and the JSON, the number of files, and each file's length and bytes. Sizes are checked before anything is read into memory: the JSON at most 4 MB, a file at most 64 MB (DES-22 §4.2), 64 files, 160 MB in all.

## Members

- **MaxFileBytes** *(field)* - The most a file may weigh on either side (DES-22 §4.2): larger is not sent, and not accepted.
- **MaxFiles** *(field)* - The most files one frame may carry.
- **MaxFrameBytes** *(field)* - The most one frame may weigh in all.
- **MaxJsonBytes** *(field)* - The most the JSON part may weigh.
- **ReadAsync** *(method)* - Reads one frame; null when the stream ended before it began.
- **WriteAsync** *(method)* - Writes one frame.
