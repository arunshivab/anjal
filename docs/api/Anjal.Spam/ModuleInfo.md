# ModuleInfo

**Namespace:** `Anjal.Spam`

Metadata for the Anjal.Spam module. Minimum-heuristics anti-spam: authentication-result scoring, sender/HELO/DNS sanity checks, simple content rules, per-tenant allow/block lists, connection rate limits and greylisting. Verdicts are recorded in headers and used to file mail in Junk; nothing here rejects mail unless explicitly configured.

## Members

- **Name** *(property)* - The module name.
- **Version** *(property)* - The version of this build, read from the compiled assembly, so it is always the version in Directory.Build.props or the one publish.ps1 stamps - never a string that can go stale. Any "+commit" suffix the SDK appends is dropped.
