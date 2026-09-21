# ModuleInfo

**Namespace:** `Anjal.Acme`

Metadata for the Anjal.Acme module: an RFC 8555 (ACME v2) client with HTTP-01 challenges, PEM certificate storage and a renewal scheduler, so an Anjal deployment obtains and renews its own TLS certificate without external tooling.

## Members

- **Name** *(property)* - The module name.
- **Version** *(property)* - The version of this build, read from the compiled assembly, so it is always the version in Directory.Build.props or the one publish.ps1 stamps - never a string that can go stale. Any "+commit" suffix the SDK appends is dropped.
