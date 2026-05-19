# DirectMailSender

**Namespace:** `Anjal.Smtp`

Outbound sender that looks up the destination domain's MX records and connects directly. Used when the host has port 25 outbound permitted. Tries each MX in priority order; falls through on transient failures.

## Members

- **#ctor** *(method)* - Construct a direct sender.
- **SendAsync** *(method)* - _(no description)_
