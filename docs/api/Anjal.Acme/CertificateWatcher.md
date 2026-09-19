# CertificateWatcher

**Namespace:** `Anjal.Acme`

Watches a and hands out the current certificate, reloading when the chain file changes. Both the SMTP server and the webmail use one of these, so a renewal by either process is picked up by the other within with no restart.

## Members

- **Reloaded** *(event)* - Raised after a new certificate has been loaded.
- **#ctor** *(method)* - Construct.
- **Dispose** *(method)* - _(no description)_
- **Invalidate** *(method)* - Force the next call to re-check the files.
- **Current** *(property)* - The current certificate, or null if none is stored. Cheap to call per connection: the file is only stat'ed once per poll interval.
- **PollInterval** *(property)* - Minimum time between file-system checks. Default 30 seconds.
