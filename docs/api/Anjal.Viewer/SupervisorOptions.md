# SupervisorOptions

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §4.1, D-358): how the supervisor runs.

## Members

- **Child** *(property)* - How a child is started.
- **ChildMemoryLimit** *(property)* - A child holding more memory than this is stopped (768 MB).
- **DicomHeapLimit** *(property)* - rc.31 (lite DICOM): the managed heap of a DICOM job's child (256 MiB), inside its 320 MB stop.
- **DicomMemoryLimit** *(property)* - rc.31 (lite DICOM, the coordinator 25 Nov 2026): every DICOM job - a preview, a slice, the patient fields and a grouping - runs in a child of its own, started for it with a heap and stopped past this (320 MB); one grouping at a time. (rc.30 had 1 GiB for a grouping alone.)
- **Log** *(property)* - Where the log goes.
- **MaxPerPerson** *(property)* - The most jobs one person may have at once, running or waiting (4).
- **MaxWaiting** *(property)* - The most jobs waiting in all (32); past it a request is answered "busy", never dropped silently.
- **ParentProcessId** *(property)* - When set, the supervisor stops when this process (the webmail that started it) is gone.
- **ReadyChildren** *(property)* - How many warmed children are kept ready (2, DES-22 §4.1).
- **ReadyWait** *(property)* - How long a child may take to warm before it is given up.
- **Running** *(property)* - How many jobs run at once (2: the service's 1536M holds two children at their 768 MB; rc.31: a DICOM job is stopped at 320 MB, so it fits beside any other).
- **SocketPath** *(property)* - The Unix socket the webmail speaks to.
